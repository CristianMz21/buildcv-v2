using BuildCv.Application.Common.Services;
using BuildCv.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildCv.Infrastructure.Persistence.Interceptors;

// Stamps the shadow audit columns, and turns a delete of an aggregate root into a tombstone.
//
// The soft-delete conversion belongs here rather than in each repository for the same reason the
// blind index does: it must not be possible for one code path to skip it. A single forgotten
// Remove() would physically destroy a resume the query filter was written to merely hide, and
// nothing about that call site would look wrong.
//
// The conversion is scoped by the presence of the DeletedAt shadow property, which only aggregate
// roots carry — but it cannot stop there. Owned navigations are eager-loaded, so loading a root pulls
// every child into the change tracker, and marking the root Deleted cascade-marks all of them Deleted
// with it. Converting only the root back to a tombstone would leave those cascades standing and
// SaveChanges would issue real DELETEs for the children: a "soft" delete that destroys the whole
// aggregate except its header. So the root's cascaded dependents are returned to Unchanged first.
//
// That restoration is deliberately scoped to the dependents still REACHABLE from the root's
// navigations, not to every Deleted owned entry in the tracker. A skill taken off a live resume is not
// a tombstone — it is a smaller resume — and that row must still really go. Because a removed child is
// no longer in the backing collection, the traversal below cannot reach it, and it stays Deleted even
// when the same SaveChanges also tombstones its root.
//
// CONSTRAINT FOR WHOEVER ADDS THE NEXT RELATIONSHIP: the traversal only restores OWNED dependents. A
// non-owned dependent reached by DeleteBehavior.Cascade is not restored, so tombstoning its principal
// would hard-delete it. Exactly one such relationship exists today — RefreshToken -> Account — and it
// is safe only by coincidence: RefreshToken is itself an aggregate root, carries its own DeletedAt,
// and is therefore tombstoned independently by this same interceptor. The first cascade dependent
// added WITHOUT HasSoftDelete() will be silently destroyed, and nothing here would catch it. Give it
// soft delete, or change DeleteBehavior, or widen this traversal — deliberately, not by default.
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _timeProvider;

    public AuditSaveChangesInterceptor(ICurrentUser currentUser, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
            return;

        var now = _timeProvider.GetUtcNow();
        var principal = _currentUser.AccountId?.Value;

        // ToList: converting a Deleted entry to Modified mutates the change tracker, and enumerating
        // Entries() lazily while doing so is undefined.
        var entries = context.ChangeTracker.Entries().ToList();

        // Built on the first tombstone and not before. Every other path through this method — which
        // is to say every ordinary insert and update — never needs it, and on a resume write the
        // dictionary would allocate one entry per row in the whole owned graph for nothing.
        Dictionary<object, EntityEntry>? tracked = null;

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    Set(entry, ShadowColumns.CreatedBy, principal);
                    Set(entry, ShadowColumns.UpdatedBy, principal);
                    break;

                case EntityState.Modified:
                    Set(entry, ShadowColumns.UpdatedBy, principal);

                    // A repository can tombstone a root by writing DeletedAt directly instead of calling
                    // Remove(), and that is how a DOMAIN delete is persisted: Account.Delete() and
                    // Organization.Delete() also change Status, and the Deleted branch below clears every
                    // modified flag before stamping, which would drop that change from the UPDATE. See
                    // TombstoneExtensions.
                    //
                    // Stamping DeletedBy here keeps "who tombstoned this row" owned by one place no
                    // matter which of the two paths wrote the timestamp.
                    if (BecomingTombstoned(entry))
                        Set(entry, ShadowColumns.DeletedBy, principal);
                    break;

                case EntityState.Deleted when Has(entry, ShadowColumns.DeletedAt):
                    // Unchanged first, NOT straight to Modified. Flipping a Deleted entry to Modified
                    // marks every property as modified, so the UPDATE would include Seq — an IDENTITY
                    // column SQL Server refuses to write. Going through Unchanged clears the flags,
                    // and assigning the three tombstone columns below re-marks exactly those.
                    //
                    // The root is un-deleted before its children so that returning a dependent to
                    // Unchanged cannot be undone by a re-cascade from a principal still marked Deleted.
                    entry.State = EntityState.Unchanged;

                    // Reference identity, not value equality. Several owned types are records, and
                    // the dictionary spans the WHOLE tracker: two resumes each holding
                    // new Language("English", "Native") are Equals to one another, so a value-keyed
                    // ToDictionary would throw on the duplicate key — on every SaveChanges, not just
                    // on a lookup that happened to collide.
                    tracked ??= entries.ToDictionary(
                        tracked => (object)tracked.Entity, tracked => tracked, ReferenceEqualityComparer.Instance);
                    RestoreCascadedDependents(entry, tracked);

                    entry.Property(ShadowColumns.DeletedAt).CurrentValue = now;
                    Set(entry, ShadowColumns.DeletedBy, principal);
                    Set(entry, ShadowColumns.UpdatedBy, principal);
                    break;
            }
        }
    }

    // Walks the owned graph hanging off a root that is being tombstoned and returns everything the
    // cascade marked Deleted back to Unchanged. Recursive because owned types can nest.
    //
    // Traversal goes through the NAVIGATIONS rather than scanning the tracker for Deleted owned
    // entries, and that is what scopes it correctly: a child that was genuinely removed from its
    // parent collection is no longer reachable here, so it keeps its Deleted state and is still hard
    // deleted — even in a SaveChanges that also tombstones its root.
    private static void RestoreCascadedDependents(
        EntityEntry root, IReadOnlyDictionary<object, EntityEntry> tracked)
    {
        foreach (var navigation in root.Navigations)
        {
            if (!navigation.Metadata.TargetEntityType.IsOwned())
                continue;

            foreach (var dependent in Targets(navigation))
            {
                if (!tracked.TryGetValue(dependent, out var entry) || entry.State != EntityState.Deleted)
                    continue;

                entry.State = EntityState.Unchanged;
                RestoreCascadedDependents(entry, tracked);
            }
        }
    }

    // What makes a removed child unreachable is that it is no longer IN the collection — not the
    // access mode. `Skills => _skills.AsReadOnly()` is a live view over the same List, so the getter
    // and the backing field agree about the removal; reading either one omits it. The access mode
    // matters for EF's writes, not for this read, and changing it would not affect the scoping.
    //
    // The `_` arm covers owned REFERENCE navigations — Resume.ContactInformation and
    // Analysis.Breakdown — which are table-split into their principal's row and whose columns are
    // NOT NULL. Missing them is what made a cascaded delete unwritable rather than merely wrong.
    private static IEnumerable<object> Targets(NavigationEntry navigation) =>
        navigation switch
        {
            CollectionEntry collection => collection.CurrentValue?.Cast<object>() ?? [],
            _ => navigation.CurrentValue is { } target ? [target] : [],
        };

    // A row acquiring its tombstone in THIS unit of work, as opposed to one that was already tombstoned
    // and is being written again for some other reason. The original/current comparison is what keeps
    // DeletedBy from being re-stamped with whoever happened to touch the row afterwards.
    private static bool BecomingTombstoned(EntityEntry entry)
    {
        if (!Has(entry, ShadowColumns.DeletedAt))
            return false;

        var deleted = entry.Property(ShadowColumns.DeletedAt);
        return deleted.IsModified && deleted.CurrentValue is not null && deleted.OriginalValue is null;
    }

    private static bool Has(EntityEntry entry, string propertyName) =>
        entry.Metadata.FindProperty(propertyName) is not null;

    private static void Set(EntityEntry entry, string propertyName, Guid? value)
    {
        if (Has(entry, propertyName))
            entry.Property(propertyName).CurrentValue = value;
    }
}

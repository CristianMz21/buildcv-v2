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
// roots carry. Owned child rows removed from their parent collection still delete for real, which is
// correct — a skill taken off a resume is not a tombstone, it is a smaller resume.
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
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    Set(entry, ShadowColumns.CreatedBy, principal);
                    Set(entry, ShadowColumns.UpdatedBy, principal);
                    break;

                case EntityState.Modified:
                    Set(entry, ShadowColumns.UpdatedBy, principal);
                    break;

                case EntityState.Deleted when Has(entry, ShadowColumns.DeletedAt):
                    // Unchanged first, NOT straight to Modified. Flipping a Deleted entry to Modified
                    // marks every property as modified, so the UPDATE would include Seq — an IDENTITY
                    // column SQL Server refuses to write. Going through Unchanged clears the flags,
                    // and assigning the three tombstone columns below re-marks exactly those.
                    entry.State = EntityState.Unchanged;
                    entry.Property(ShadowColumns.DeletedAt).CurrentValue = now;
                    Set(entry, ShadowColumns.DeletedBy, principal);
                    Set(entry, ShadowColumns.UpdatedBy, principal);
                    break;
            }
        }
    }

    private static bool Has(EntityEntry entry, string propertyName) =>
        entry.Metadata.FindProperty(propertyName) is not null;

    private static void Set(EntityEntry entry, string propertyName, Guid? value)
    {
        if (Has(entry, propertyName))
            entry.Property(propertyName).CurrentValue = value;
    }
}

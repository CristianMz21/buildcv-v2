using BuildCv.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Translates a DOMAIN delete into the persistence tombstone.
//
// Two independent delete concepts used to exist side by side. `Account.Delete()` and
// `Organization.Delete()` set Status = Deleted and never touch DeletedAt; `DbContext.Remove()` writes
// DeletedAt through AuditSaveChangesInterceptor and never touches Status. The filtered unique index on
// Accounts.EmailHash is written against the SECOND one — "a soft-deleted account frees its address for
// re-registration" — so a domain delete left the row fully visible and the address locked forever.
//
// The ruling is that Delete() marks both, and this is where the second mark is applied: the repository
// sees an aggregate whose status is Deleted and stamps the tombstone alongside it.
//
// It deliberately does NOT go through Remove(). The audit interceptor gets a Deleted entry back to
// Unchanged before stamping, which clears every modified flag — so the Status change that motivated the
// delete would be dropped from the UPDATE and the row would be tombstoned while still reading Active.
// Writing DeletedAt on an entry that stays Modified keeps both marks in one statement, and, because the
// entry is never Deleted, EF never cascades to the owned children the interceptor would have to rescue.
internal static class TombstoneExtensions
{
    // Idempotent: an aggregate that is already tombstoned keeps the timestamp it was tombstoned with.
    // Overwriting it would move the audit record every time a deleted row was saved again.
    //
    // There is no inverse. A tombstoned root is invisible to every repository read, so nothing can load
    // one to restore it — reviving an account is a purge-and-re-register decision, not an UPDATE.
    public static void MarkTombstoned(this EntityEntry entry, DateTimeOffset deletedAt)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var deleted = entry.Property(ShadowColumns.DeletedAt);
        if (deleted.CurrentValue is null)
            deleted.CurrentValue = deletedAt;
    }
}

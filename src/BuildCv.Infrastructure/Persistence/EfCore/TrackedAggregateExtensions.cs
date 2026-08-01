using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Every UpdateAsync starts here: an aggregate this context is not tracking cannot be written, and the
// repository says so instead of trying.
//
// The obvious alternative — Update(entity) on the detached instance — looks like a courtesy and is a
// trap. RowVersion is SHADOW state, so a detached instance carries none: the generated UPDATE compares
// against NULL, matches no row, and surfaces as a ConcurrencyConflictException that blames a concurrent
// writer for what is actually a caller passing the wrong object. On a root with owned collections it is
// worse than useless — Update() marks every child Added, because their shadow keys are unset too, so the
// batch attempts INSERTs for rows that already exist before the root's UPDATE fails. Nothing is
// corrupted, only because SaveChanges is transactional.
//
// So the fallback is a throw, and the message names the fix rather than the symptom. In practice this is
// unreachable: the repositories are scoped, every read that feeds a mutation is AsTracking(), and the
// handlers pass back the instance they loaded.
internal static class TrackedAggregateExtensions
{
    public static EntityEntry<TEntity> RequireTracked<TEntity>(this BuildCvDbContext context, TEntity entity)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);

        var entry = context.Entry(entity);

        return entry.State is EntityState.Detached
            ? throw new InvalidOperationException(
                $"UpdateAsync requires the {typeof(TEntity).Name} instance returned by this repository; "
                + "a detached aggregate carries no rowversion and cannot be written safely.")
            : entry;
    }
}

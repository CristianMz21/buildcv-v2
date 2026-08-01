using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// The one save every EF repository calls, and the only place a provider-specific failure is turned
// into something the rest of the application can catch.
//
// It lives here rather than in each repository because the translation is not optional: a repository
// that forgets it lets a raw SqlException with a vendor error number escape into the Api, where the
// only handler that matches is the 500 fallback. "This address is taken" would read as an outage.
internal static class SaveChangesExtensions
{
    // Unique CONSTRAINT violation (2627) and unique INDEX violation (2601). Both are the same event
    // from the caller's point of view — a uniqueness rule rejected the write — and the blind-index
    // columns are enforced by the latter.
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    public static async Task SaveTranslatingFailuresAsync(
        this BuildCvDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        // Must be caught before DbUpdateException: it derives from it, and the ordering is what
        // decides whether a lost update is reported as a conflict or as a duplicate.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException(
                "The record was modified by another request before this write committed.", exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Deliberately says nothing about WHICH value collided. The unique indexes that fire here
            // are over blind-index digests, and naming the offending value would put a login
            // identifier into an error string that ends up in a log and in an HTTP response.
            throw new DuplicateKeyException("A record with the same unique value already exists.", exception);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation };
}

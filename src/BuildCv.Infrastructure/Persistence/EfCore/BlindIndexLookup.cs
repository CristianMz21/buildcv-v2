using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// The only supported way for a repository to find a row by a blind index.
//
// The first parameter is the whole point of the type. It is IReadOnlyList<byte[]> — the shape
// ComputeCandidates returns — and there is no overload taking a single byte[], so the digest produced
// by Compute cannot reach a read path: it does not type-check.
//
// That matters because the failure it prevents is silent. Compute returns only the ACTIVE key's digest.
// Use it on a lookup and, for the whole of a key-rotation window, every row still carrying the retired
// digest answers "no such account" — which is not merely a failed login: registration's duplicate check
// runs through the same lookup, so re-registering that address SUCCEEDS. The new digest does not collide
// with the old one under the unique index, and the account is quietly duplicated.
//
// One query per configured key rather than a single IN over all of them. The candidate list has at most
// BlindIndexKeyRing.MaxKeys entries, the active key is first so the common case matches on the first
// round trip, and each query is a plain equality seek on the unique index — the plan this column exists
// to get. Folding them into one predicate hands the provider a parameter collection instead, which is
// both harder to guarantee for varbinary and free to choose a scan.
internal static class BlindIndexLookup
{
    public static async Task<TEntity?> FirstMatchAsync<TEntity>(
        IReadOnlyList<byte[]> candidates,
        Func<byte[], IQueryable<TEntity>> query,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(query);

        foreach (var digest in candidates)
        {
            var match = await query(digest).FirstOrDefaultAsync(cancellationToken);
            if (match is not null)
                return match;
        }

        return null;
    }

    public static async Task<bool> AnyMatchAsync<TEntity>(
        IReadOnlyList<byte[]> candidates,
        Func<byte[], IQueryable<TEntity>> query,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(query);

        foreach (var digest in candidates)
        {
            if (await query(digest).AnyAsync(cancellationToken))
                return true;
        }

        return false;
    }
}

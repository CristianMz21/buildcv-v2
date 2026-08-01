using BuildCv.Application.Common.Pagination;
using BuildCv.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Keyset pagination over the shadow Seq column — the reason any of this is expressible in SQL at all.
//
// The public ids are value-converted strongly-typed Guids. EF translates `Id == x` on them, but not
// `Id > x`: an ordering comparison on a converted value has no SQL equivalent, so an id-based cursor
// would be evaluated client-side, which means reading the whole table first. Seq is a plain bigint
// IDENTITY, it is the CLUSTERED key, and every root's list index is (key, Seq), so
// `WHERE key = @k AND Seq < @cursor ORDER BY Seq DESC` is a seek straight to the page boundary and a
// scan of Limit + 1 rows — the same cost on page one and on page ten thousand. OFFSET/FETCH would have
// had to count past every skipped row to find the same page, and would still shift under concurrent
// inserts.
//
// The entity is projected ALONGSIDE its Seq because Seq is shadow state: once EF has materialized a
// Resume there is no way left to ask where it sat, and that number is exactly the next cursor. Owned
// navigations still travel with the entity through this projection — ResumeRepositoryTests walks a page
// and asserts the ten collections arrive, so that is checked rather than assumed.
internal static class KeysetQueryExtensions
{
    public static Task<Page<TEntity>> ToNewestFirstPageAsync<TEntity>(
        this IQueryable<TEntity> source, PageRequest page, CancellationToken cancellationToken)
        where TEntity : class =>
        RunAsync(source.NewestFirstProbe(page), page, cancellationToken);

    public static Task<Page<TEntity>> ToOldestFirstPageAsync<TEntity>(
        this IQueryable<TEntity> source, PageRequest page, CancellationToken cancellationToken)
        where TEntity : class =>
        RunAsync(source.OldestFirstProbe(page), page, cancellationToken);

    // The two Probe methods stop one step short of executing so the SQL can be READ without a database:
    // KeysetQueryTranslationTests calls ToQueryString on them and asserts the cursor comparison and the
    // row cap are in the statement. That is the failure nothing else would catch — a predicate that
    // quietly fell back to client evaluation still returns the right page, after reading the table.
    public static IQueryable<KeysetRow<TEntity>> NewestFirstProbe<TEntity>(
        this IQueryable<TEntity> source, PageRequest page)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(page);

        // Applied as a separate Where only when there IS a cursor, rather than as
        // `@cursor IS NULL OR Seq < @cursor` in one predicate. The one-predicate form is shorter and
        // hands the optimizer a single plan that has to serve both shapes; two statements let each get
        // the seek it deserves.
        if (page.Cursor is { } cursor)
            source = source.Where(entity => EF.Property<long>(entity, ShadowColumns.Seq) < cursor.Position);

        return Probe(source.OrderByDescending(entity => EF.Property<long>(entity, ShadowColumns.Seq)), page);
    }

    public static IQueryable<KeysetRow<TEntity>> OldestFirstProbe<TEntity>(
        this IQueryable<TEntity> source, PageRequest page)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(page);

        if (page.Cursor is { } cursor)
            source = source.Where(entity => EF.Property<long>(entity, ShadowColumns.Seq) > cursor.Position);

        return Probe(source.OrderBy(entity => EF.Property<long>(entity, ShadowColumns.Seq)), page);
    }

    // Limit + 1: the extra row is never returned, it only answers "is there another page?" without a
    // second round trip and without a COUNT over the whole owner. Page<T>.From owns what happens to it.
    private static IQueryable<KeysetRow<TEntity>> Probe<TEntity>(
        IOrderedQueryable<TEntity> ordered, PageRequest page)
        where TEntity : class =>
        ordered
            .Select(entity => new KeysetRow<TEntity>(entity, EF.Property<long>(entity, ShadowColumns.Seq)))
            .Take(page.Limit + 1);

    private static async Task<Page<TEntity>> RunAsync<TEntity>(
        IQueryable<KeysetRow<TEntity>> probe, PageRequest page, CancellationToken cancellationToken)
        where TEntity : class =>
        Page<TEntity>.From(await probe.ToListAsync(cancellationToken), page);
}

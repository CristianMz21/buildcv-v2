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
// and asserts the ten collections arrive, so that is checked rather than assumed. HOW they travel is
// the other half of the story, and it is not free: see Probe.
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
    //
    // AsSplitQuery, and on a paged read it is the difference between bounded and unbounded. Every entity
    // paged here carries owned collections — Resume has ten, JobPosting has three, Analysis has one — and
    // owned navigations load eagerly. In one statement that is a LEFT JOIN per collection onto the same
    // principal, and the server returns their CARTESIAN PRODUCT: rows shipped is the sum, over the page,
    // of the PRODUCT of each principal's collection counts. TOP caps the principals inside the subquery,
    // so the fan-out happens outside it and the cap does not reach it. Nothing caps any collection, so a
    // client that posts enough child rows chooses that number. Split query asks one statement per
    // collection, which makes the work the SUM of the counts and puts an actual ceiling on a page.
    //
    // Those three counts are prose, and this sentence has already gone stale once: JobPosting gained
    // LanguageRequirements and the line kept saying two. Nothing breaks when it does —
    // KeysetQueryTranslationTests reads the real number off the model via OwnedCollectionsOn, so the
    // assertion cannot drift with the comment — but the sentence is what a reader budgets against, so
    // it is worth correcting rather than deleting.
    //
    // The cost is stated rather than hidden: the statements are not one atomic read, so a page can
    // observe a collection edit that landed between them. That is acceptable here — these lists are
    // edited by their own owner, and the alternative is a query whose size an unauthenticated row count
    // decides. Score history is the case that shows why this belongs in the shared probe rather than in
    // one repository: Analysis owned no collections when this was written and owns Recommendations now,
    // so a page of twenty would have fanned out the moment that mapping landed, with no code here
    // changing and nothing to notice it.
    //
    // Applied AFTER the projection deliberately. Split query has real restrictions with projections, so
    // whether it survives KeysetRow<T> is a question to answer rather than assume — the SQL is read in
    // KeysetQueryTranslationTests, which counts the joins and re-checks the row cap on the split shape.
    //
    // Take with split query needs a total order or EF cannot align the follow-up statements. Seq is a
    // bigint IDENTITY and unique, so the ORDER BY above already is one; EF appends the key as a
    // tie-break anyway, which is redundant here rather than load-bearing.
    private static IQueryable<KeysetRow<TEntity>> Probe<TEntity>(
        IOrderedQueryable<TEntity> ordered, PageRequest page)
        where TEntity : class =>
        ordered
            .Select(entity => new KeysetRow<TEntity>(entity, EF.Property<long>(entity, ShadowColumns.Seq)))
            .Take(page.Limit + 1)
            .AsSplitQuery();

    private static async Task<Page<TEntity>> RunAsync<TEntity>(
        IQueryable<KeysetRow<TEntity>> probe, PageRequest page, CancellationToken cancellationToken)
        where TEntity : class =>
        Page<TEntity>.From(await probe.ToListAsync(cancellationToken), page);
}

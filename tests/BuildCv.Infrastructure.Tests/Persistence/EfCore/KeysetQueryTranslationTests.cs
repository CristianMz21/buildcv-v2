using System.Text.RegularExpressions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.EfCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// Reads the SQL the keyset queries produce. No database: building a query and asking for its text does
// not open a connection, so this stays out of the Integration category and runs in milliseconds.
//
// It exists because the integration tests CANNOT fail for the reason that matters most. A cursor
// predicate that fell back to client evaluation would still return exactly the right page — after
// pulling every row of the table into memory to do it — and every page-shape assertion would stay
// green while the query got linearly slower with the data. The statement is the only place that shows.
public sealed class KeysetQueryTranslationTests
{
    [Fact]
    public void NewestFirstProbe_WithACursor_PutsTheBoundaryAndTheRowCapInTheStatement()
    {
        using var context = PersistenceTestContext.ModelOnly();
        var owner = AccountId.New();

        var sql = context.Resumes
            .Where(resume => resume.OwnerId == owner)
            .NewestFirstProbe(PageRequests.Of(20, Cursor.At(500).Encode()))
            .ToQueryString();

        sql.Should().MatchRegex(@"\[Seq\] < ", "the cursor boundary has to be evaluated by the server");
        sql.Should().MatchRegex(@"TOP\(@\w+\)", "the cap has to be a row cap, not a client-side Take");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC");

        // The VALUE, not just the presence of a TOP. A regression to Take(page.Limit) — dropping the
        // probe row, so the last page always reports a successor it does not have — leaves a TOP in the
        // statement and would sail past a Contain("TOP(") assertion.
        RowCapIn(sql).Should().Be(21, "twenty asked for, plus the one row that answers 'is there more?'");
    }

    // ToQueryString emits the parameters as DECLARE statements above the query, which is the only place
    // the cap's actual value appears.
    private static int RowCapIn(string sql)
    {
        var declaration = Regex.Match(sql, @"TOP\((@\w+)\)");
        declaration.Success.Should().BeTrue("the statement has to carry a parameterised row cap");

        var value = Regex.Match(sql, $@"DECLARE {Regex.Escape(declaration.Groups[1].Value)} int = (\d+);");
        value.Success.Should().BeTrue("the row cap parameter has to be declared with a literal value");

        return int.Parse(value.Groups[1].Value);
    }

    [Fact]
    public void OldestFirstProbe_WithACursor_ComparesInTheOtherDirection()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = context.Analyses
            .NewestFirstProbe(PageRequests.Of(20, Cursor.At(500).Encode()))
            .ToQueryString();

        var forwards = context.Analyses
            .OldestFirstProbe(PageRequests.Of(20, Cursor.At(500).Encode()))
            .ToQueryString();

        sql.Should().MatchRegex(@"\[Seq\] < ");
        forwards.Should().MatchRegex(@"\[Seq\] > ");
        forwards.Should().NotContain("DESC");
    }

    // The first page has no boundary to apply, so the predicate must be ABSENT rather than a comparison
    // against a null parameter — that is the shape that would give both pages one plan and neither the
    // right one.
    [Fact]
    public void NewestFirstProbe_WithoutACursor_EmitsNoBoundaryPredicateButStillCapsTheRows()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = context.Resumes.NewestFirstProbe(PageRequests.Of(20)).ToQueryString();

        sql.Should().NotContain("[Seq] <");
        RowCapIn(sql).Should().Be(21);
    }

    // The clamp reaches the SQL, not just the PageRequest: a caller asking for ten thousand rows must
    // produce TOP(101), never TOP(10001).
    [Fact]
    public void NewestFirstProbe_WithALimitBeyondTheCeiling_CapsTheStatementAtTheMaximum()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = context.Resumes.NewestFirstProbe(PageRequests.Of(10_000)).ToQueryString();

        RowCapIn(sql).Should().Be(PageRequest.MaxLimit + 1);
    }

    // The soft-delete filter is a global query filter, so it survives anything composed on top of it —
    // but a paged list is exactly where a tombstoned row reappearing would be least noticed, since the
    // page would simply be one row different.
    [Fact]
    public void NewestFirstProbe_StillCarriesTheSoftDeleteFilter()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = context.Resumes.NewestFirstProbe(PageRequests.Of(20)).ToQueryString();

        sql.Should().Contain("[DeletedAt] IS NULL");
    }

    // The assertion this file was missing, and the one that makes the row cap mean something.
    //
    // TOP caps the PRINCIPALS. Joining the owned collections in the same statement fans out after that
    // cap: rows shipped is the sum, over the page, of the PRODUCT of each principal's collection counts.
    // A resume with ten modestly filled collections is already thousands of rows, times a page of
    // twenty, and no collection is capped by anything — so a client picks the number. EF de-duplicates
    // on materialization, so every page-shape assertion in this suite and every integration test passes
    // either way. The join count is the only place the difference exists.
    [Fact]
    public void NewestFirstProbe_OverResumes_JoinsNoneOfTheTenOwnedCollections()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = context.Resumes.NewestFirstProbe(PageRequests.Of(20)).ToQueryString();

        JoinsIn(sql).Should().Be(0, "each owned collection has to be fetched by its own statement");
        RowCapIn(sql).Should().Be(21, "splitting must not cost the cap that bounds the principals");
        sql.Should().MatchRegex(@"\[Seq\] DESC", "nor the order the cursor walks");
    }

    // Not only resumes. JobPosting owns two collections and is paged through the same helper, so the
    // fix belongs in the shared probe rather than in ResumeRepository — a per-repository fix would leave
    // this list quietly multiplying Requirements by Responsibilities.
    [Fact]
    public void NewestFirstProbe_OverJobPostings_JoinsNeitherOwnedCollection()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = context.JobPostings.NewestFirstProbe(PageRequests.Of(20)).ToQueryString();

        JoinsIn(sql).Should().Be(0);
        RowCapIn(sql).Should().Be(21);
    }

    // The counterfactual, because "zero joins" is only evidence if the query would otherwise have had
    // them. Without this, a refactor that stopped loading the collections at all would leave the two
    // assertions above green while breaking every consumer. It also states the size of what was removed:
    // ten tables for a resume, two for a job posting, each multiplying against the others.
    [Fact]
    public void NewestFirstProbe_WithoutSplitting_WouldJoinEveryOwnedCollection()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var resumes = context.Resumes.NewestFirstProbe(PageRequests.Of(20)).AsSingleQuery().ToQueryString();
        var jobs = context.JobPostings.NewestFirstProbe(PageRequests.Of(20)).AsSingleQuery().ToQueryString();

        JoinsIn(resumes).Should().Be(
            OwnedCollectionsOn(context, typeof(Resume)),
            "this is the shape being rejected: one join per collection, multiplying into a product");
        JoinsIn(jobs).Should().Be(OwnedCollectionsOn(context, typeof(JobPosting)));
    }

    // ToQueryString renders only the FIRST statement of a split query and appends a note saying so, so
    // the note is what proves the collections went somewhere rather than being dropped. Asserted for
    // Analysis too — it owns no collections, so splitting changes nothing about its statement, and
    // pinning that keeps the page shared by all three lists rather than special-cased per entity.
    [Fact]
    public void OldestFirstProbe_OverAnEntityWithNoCollections_IsStillTheSameSingleStatement()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = context.Analyses.OldestFirstProbe(PageRequests.Of(20)).ToQueryString();

        JoinsIn(sql).Should().Be(0);
        RowCapIn(sql).Should().Be(21);
        sql.Should().Contain("split-query mode");
    }

    private static int JoinsIn(string sql) => sql.Split("JOIN", StringSplitOptions.None).Length - 1;

    private static int OwnedCollectionsOn(BuildCvDbContext context, Type entity) =>
        context.Model.FindEntityType(entity)!.GetNavigations().Count(navigation => navigation.IsCollection);
}

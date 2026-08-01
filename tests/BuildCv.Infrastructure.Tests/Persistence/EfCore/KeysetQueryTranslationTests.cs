using System.Text.RegularExpressions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Domain.Identity;
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
}

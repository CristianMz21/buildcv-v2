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
        sql.Should().Contain("TOP(", "the limit+1 cap has to be a row cap, not a client-side Take");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC");
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
        sql.Should().Contain("TOP(");
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

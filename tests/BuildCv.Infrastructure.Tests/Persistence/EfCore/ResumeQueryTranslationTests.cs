using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.EfCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// Reads the SQL the resume reads produce. No database: building a query and asking for its text does
// not open a connection, so this stays out of the Integration category and runs in milliseconds.
//
// It exists because the integration tests CANNOT fail for the reason that matters. Resume has ten owned
// collections; loading it in one statement LEFT JOINs all ten against the same principal and asks the
// server for their CARTESIAN PRODUCT. EF then de-duplicates during materialization, so the object graph
// that comes back is identical either way — ResumeRepositoryTests loads a fully populated resume and
// every assertion passes on both shapes. The cost is entirely in rows the server built and shipped, and
// the statement is the only place it is visible.
public sealed class ResumeQueryTranslationTests
{
    [Fact]
    public void ByIdQuery_JoinsNoneOfTheOwnedCollections()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = ResumeRepository.ByIdQuery(context, ResumeId.New()).ToQueryString();

        JoinsIn(sql).Should().Be(0, "the ten owned collections must each get their own statement");
    }

    // The counterfactual, in the same test file, because "zero joins" is only evidence if the query
    // WOULD have had joins. Without this a future refactor that stopped loading the collections at all
    // would leave the assertion above green while quietly breaking every consumer.
    [Fact]
    public void ByIdQuery_WithoutSplitting_WouldJoinEveryOwnedCollection()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = ResumeRepository.ByIdQuery(context, ResumeId.New()).AsSingleQuery().ToQueryString();

        JoinsIn(sql).Should().Be(
            OwnedCollectionsOnResume(context),
            "this is the shape being rejected: one join per collection, multiplying into a product");
    }

    // ToQueryString renders only the FIRST statement of a split query and appends a note saying so.
    // Asserted separately from the join count so a regression is legible: a statement with joins in it
    // is the single-query shape, a statement without this note is not split at all.
    [Fact]
    public void ByIdQuery_IsExecutedAsASplitQuery()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = ResumeRepository.ByIdQuery(context, ResumeId.New()).ToQueryString();

        sql.Should().Contain("split-query mode");
    }

    // The predicate still has to reach the server. Splitting changes how the collections are fetched,
    // not which principal is looked up, and a filter that fell to client evaluation would read the table.
    [Fact]
    public void ByIdQuery_StillFiltersOnTheServerAndCarriesTheSoftDeleteFilter()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var sql = ResumeRepository.ByIdQuery(context, ResumeId.New()).ToQueryString();

        sql.Should().Contain("[Id] = ");
        sql.Should().Contain("[DeletedAt] IS NULL");
    }

    private static int JoinsIn(string sql) =>
        sql.Split("JOIN", StringSplitOptions.None).Length - 1;

    private static int OwnedCollectionsOnResume(BuildCvDbContext context) =>
        context.Model.FindEntityType(typeof(Resume))!
            .GetNavigations()
            .Count(navigation => navigation.IsCollection);
}

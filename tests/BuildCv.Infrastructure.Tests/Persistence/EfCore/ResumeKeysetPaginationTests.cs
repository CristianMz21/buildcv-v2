using BuildCv.Application.Common.Pagination;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// Keyset pagination against the real thing.
//
// Everything the boundary arithmetic does is already pinned in the Application tests, in milliseconds.
// What only a database can answer is whether the query TRANSLATES: `Seq < @cursor` on a shadow bigint,
// ordered descending, projected alongside the entity, with a limit+1 Take. If any of that fell back to
// client evaluation the tests below would still pass — but they would be reading the whole table to do
// it — so the page-shape assertions here are the sanity check and the translation is what is really
// under test.
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ResumeKeysetPaginationTests
{
    private readonly SqlServerFixture _fixture;

    public ResumeKeysetPaginationTests(SqlServerFixture fixture) => _fixture = fixture;

    // Five resumes at two per page is three pages: 2, 2, 1. Another owner's resume is interleaved
    // between every one of them, so a query that lost its WHERE would not merely return extra rows, it
    // would shift every page boundary and the walk would fail on sizes before it failed on contents.
    [Fact]
    public async Task GetPageByOwnerIdAsync_WalkedByCursor_VisitsEveryResumeOnceNewestFirst()
    {
        var owner = AccountId.New();
        var noise = AccountId.New();
        var mine = new List<ResumeId>();

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Resumes(writer);
            for (var index = 0; index < 5; index++)
            {
                var resume = NewResume(owner);
                await repository.AddAsync(resume);
                mine.Add(resume.Id);
                await repository.AddAsync(NewResume(noise));
            }
        }

        mine.Reverse();

        await using var reader = _fixture.NewApplicationContext();
        var repositoryForReads = TestRepositories.Resumes(reader);

        var visited = new List<ResumeId>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await repositoryForReads.GetPageByOwnerIdAsync(owner, PageRequests.Of(2, cursor));
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items.Select(resume => resume.Id));
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 2, 1);
        visited.Should().Equal(mine, "no gap, no repeat, newest first");
        visited.Should().OnlyHaveUniqueItems();
    }

    // The boundary the odd-numbered walk above can never reach: the page that ends exactly on the last
    // row. The probe row is what tells the difference, and getting it wrong hands the caller a cursor
    // for a page that will always come back empty.
    [Fact]
    public async Task GetPageByOwnerIdAsync_WhenTheLastPageIsExactlyFull_ReportsNoNextPage()
    {
        var owner = AccountId.New();

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Resumes(writer);
            for (var index = 0; index < 4; index++)
                await repository.AddAsync(NewResume(owner));
        }

        await using var reader = _fixture.NewApplicationContext();
        var repositoryForReads = TestRepositories.Resumes(reader);

        var firstPage = await repositoryForReads.GetPageByOwnerIdAsync(owner, PageRequests.Of(2));
        firstPage.Items.Should().HaveCount(2);
        firstPage.NextCursor.Should().NotBeNull();

        var secondPage = await repositoryForReads.GetPageByOwnerIdAsync(
            owner, PageRequests.Of(2, firstPage.NextCursor));

        secondPage.Items.Should().HaveCount(2);
        secondPage.NextCursor.Should().BeNull("four resumes at two per page end on the second");
    }

    // The ceiling has to hold at the port, not just at the endpoint: a caller that asks for ten thousand
    // rows gets MaxLimit, and one that asks for zero gets a row rather than an empty page it would
    // never escape.
    [Fact]
    public async Task GetPageByOwnerIdAsync_ClampsTheLimitAtTheDatabase()
    {
        var owner = AccountId.New();

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Resumes(writer);
            for (var index = 0; index < 3; index++)
                await repository.AddAsync(NewResume(owner));
        }

        await using var reader = _fixture.NewApplicationContext();
        var repositoryForReads = TestRepositories.Resumes(reader);

        (await repositoryForReads.GetPageByOwnerIdAsync(owner, PageRequests.Of(0)))
            .Items.Should().ContainSingle("a limit of zero clamps up to one, it does not page nothing forever");

        var generous = await repositoryForReads.GetPageByOwnerIdAsync(owner, PageRequests.Of(1000));
        generous.Items.Should().HaveCount(3);
        generous.NextCursor.Should().BeNull();
        PageRequests.Of(1000).Limit.Should().Be(PageRequest.MaxLimit);
    }

    [Fact]
    public async Task GetPageByOwnerIdAsync_ForAnOwnerWithNoResumes_IsAnEmptyFinalPage()
    {
        await using var reader = _fixture.NewApplicationContext();

        var page = await TestRepositories.Resumes(reader).GetPageByOwnerIdAsync(AccountId.New(), PageRequests.Of());

        page.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    // A cursor from beyond the end is well formed and points at nothing, which is what a client holds
    // after somebody deletes the row it was resting on. It has to answer an empty final page rather than
    // wrapping around to the top.
    [Fact]
    public async Task GetPageByOwnerIdAsync_WithACursorPastTheEndOfTheWalk_IsAnEmptyFinalPage()
    {
        var owner = AccountId.New();

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Resumes(writer).AddAsync(NewResume(owner));

        await using var reader = _fixture.NewApplicationContext();

        // Position 1 is the very first row the table ever issued, so nothing sorts below it.
        var page = await TestRepositories.Resumes(reader)
            .GetPageByOwnerIdAsync(owner, PageRequests.Of(10, Cursor.At(1).Encode()));

        page.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    private static Resume NewResume(AccountId ownerId) =>
        Resume.Create(ownerId, new ContactInformation(
            PersonName.Create("Test Person"), Email.Create($"page.{Guid.NewGuid():N}@example.com")));
}

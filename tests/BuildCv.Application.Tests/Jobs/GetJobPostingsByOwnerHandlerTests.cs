namespace BuildCv.Application.Tests.Jobs;

using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Jobs;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using FluentAssertions;

public sealed class GetJobPostingsByOwnerHandlerTests
{
    private readonly FakeJobPostingRepository _postings = new();
    private readonly GetJobPostingsByOwnerHandler _handler;

    public GetJobPostingsByOwnerHandlerTests() => _handler = new GetJobPostingsByOwnerHandler(_postings);

    // NEWEST FIRST, the repo's ordinary direction — this is an inventory of what the candidate is
    // chasing, not a history to replay forwards like the score and readability lists.
    [Fact]
    public async Task Handle_WithNoPagingArguments_ReturnsTheCallersPostingsNewestFirst()
    {
        var owner = AccountId.New();
        var first = await Seed(owner, "Backend Developer");
        var second = await Seed(owner, "Platform Engineer");
        var third = await Seed(owner, "Staff Engineer");

        var page = await Page(owner);

        page.Items.Select(posting => posting.Id).Should().Equal(third.Id, second.Id, first.Id);
        page.NextCursor.Should().BeNull();
    }

    // THE ISOLATION THAT MATTERS MOST ON THIS ROUTE. A posting belongs to whoever owns it and to nobody
    // else, and this list is the one place a missing owner filter would hand every account's private
    // offers to the first caller — a Draft offer names the opportunity a candidate is chasing.
    [Fact]
    public async Task Handle_WithAnotherAccountsPostingsInTheStore_ReturnsNoneOfThem()
    {
        var owner = AccountId.New();
        var mine = await Seed(owner, "Backend Developer");
        var somebodyElses = await Seed(AccountId.New(), "Somebody Elses Offer");

        var page = await Page(owner);

        page.Items.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
        page.Items.Should().NotContain(posting => posting.Id == somebodyElses.Id);
    }

    // EVERY POSTING THE CALLER OWNS, whichever route created it — the decision written on the handler.
    // Both creation paths call the same factory and set the same OwnerId, so a filter that tried to
    // return "only imported offers" would have to guess from Status or CompanyId; these two rows are
    // exactly the pair such a guess would separate, and they must come back together.
    [Fact]
    public async Task Handle_ForARecruiterWhoAlsoImportedAnOffer_ReturnsBothKindsOfPosting()
    {
        var owner = AccountId.New();
        var candidateStyleOffer = await Seed(owner, "Imported Offer");
        var recruiterStylePosting = await Seed(owner, "Recruiter Posting");
        recruiterStylePosting.Publish();
        await _postings.UpdateAsync(recruiterStylePosting);

        var page = await Page(owner);

        page.Items.Select(posting => posting.Id)
            .Should().BeEquivalentTo(new[] { candidateStyleOffer.Id, recruiterStylePosting.Id });

        // Stated rather than implied: the two really are distinguishable by Status, which is the proxy a
        // narrower filter would reach for. Both are here anyway.
        page.Items.Select(posting => posting.Status)
            .Should().Contain(JobPostingStatus.Draft).And.Contain(JobPostingStatus.Published);
    }

    // Newest first, so the cursor walks BACKWARD in time and page two must start strictly before the row
    // page one ended on. Another account's postings sit in the store the whole time.
    [Fact]
    public async Task Handle_WalkedByCursor_VisitsEveryPostingExactlyOnce()
    {
        var owner = AccountId.New();
        var created = new List<JobPostingId>();
        for (var index = 0; index < 5; index++)
            created.Add((await Seed(owner, $"Role {index}")).Id);

        var stranger = await Seed(AccountId.New(), "Somebody Elses Offer");

        var visited = new List<JobPostingId>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await Page(owner, limit: 2, cursor);
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items.Select(posting => posting.Id));
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 2, 1);
        created.Reverse();
        visited.Should().Equal(created);
        visited.Should().NotContain(stranger.Id);
    }

    // The LITERAL, for the reason GetAnalysisHistoryHandlerTests states: written as
    // HaveCount(PageRequest.MaxLimit) over a seed of MaxLimit + 1 this stays green when the ceiling
    // moves, because both sides of the comparison move together. What it covers is the HANDLER, which
    // could stop routing the caller's limit through PageRequest.Create at all.
    [Fact]
    public async Task Handle_WithALimitBeyondTheCeiling_ClampsItToOneHundred()
    {
        var owner = AccountId.New();
        for (var index = 0; index < 101; index++)
            await Seed(owner, $"Role {index}");

        var page = await Page(owner, limit: 100_000);

        page.Items.Should().HaveCount(100, "PageRequest.MaxLimit is the ceiling and the handler cannot bypass it");
        page.NextCursor.Should().NotBeNull("the 101st row is still there to be walked to");
    }

    [Fact]
    public async Task Handle_ForAnAccountWithNoPostings_IsAnEmptyFinalPage()
    {
        await Seed(AccountId.New(), "Somebody Elses Offer");

        var page = await Page(AccountId.New());

        page.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    // A cursor the caller invented is refused rather than silently restarting the walk from the top,
    // which would read as data loss to a client halfway through a list.
    [Theory]
    [InlineData("nonsense")]
    [InlineData("AAAAAAAAAAA")]
    [InlineData("%00%00")]
    public async Task Handle_WithACursorItCannotDecode_Fails(string cursor)
    {
        var owner = AccountId.New();
        await Seed(owner, "Backend Developer");

        var result = await _handler.Handle(new GetJobPostingsByOwnerQuery(owner, 10, cursor));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PageRequest.InvalidCursorError);
        result.Value.Should().BeNull();
    }

    private async Task<Page<JobPosting>> Page(AccountId requester, int? limit = null, string? cursor = null)
    {
        var result = await _handler.Handle(new GetJobPostingsByOwnerQuery(requester, limit, cursor));
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    private async Task<JobPosting> Seed(AccountId owner, string title)
    {
        var posting = JobPosting.Create(owner, title, OrganizationName.Create("Contoso"));
        await _postings.AddAsync(posting);
        return posting;
    }
}

namespace BuildCv.Application.Tests.Scoring;

using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Scoring;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

public sealed class GetAnalysisHistoryHandlerTests
{
    private readonly FakeResumeRepository _resumes = new();
    private readonly FakeAnalysisRepository _analyses = new();
    private readonly GetAnalysisHistoryHandler _handler;

    public GetAnalysisHistoryHandlerTests() => _handler = new GetAnalysisHistoryHandler(_resumes, _analyses);

    // OLDEST FIRST, the one list in this repo that does not page newest first. Every other paged
    // assertion in these tests reads the other way round, so this is the direction a refactor is most
    // likely to "correct" — and the product breaks silently when it does: the history stops being a
    // story of what changed and becomes an unordered pile with the newest entry on top.
    [Fact]
    public async Task Handle_WithNoPagingArguments_ReturnsTheResumesHistoryOldestFirst()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var first = await SeedAnalysis(resume.Id);
        var second = await SeedAnalysis(resume.Id);
        var third = await SeedAnalysis(resume.Id);

        var page = await Page(owner, resume.Id);

        page.Items.Select(analysis => analysis.Id).Should().Equal(first.Id, second.Id, third.Id);
        page.NextCursor.Should().BeNull();
    }

    // Forwards, not backwards, and the cursor boundary flips with the direction: a `<` copied from the
    // newest-first path would hand back the same row twice and then run out. Another resume's history
    // sits in the store the whole time and must never appear on any page.
    [Fact]
    public async Task Handle_WalkedByCursor_ReplaysTheHistoryForwardsExactlyOnce()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var history = new List<AnalysisId>();
        for (var index = 0; index < 5; index++)
            history.Add((await SeedAnalysis(resume.Id)).Id);

        var otherResume = await SeedResume(owner);
        var anotherResumesScore = await SeedAnalysis(otherResume.Id);

        var visited = new List<AnalysisId>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await Page(owner, resume.Id, limit: 2, cursor);
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items.Select(analysis => analysis.Id));
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 2, 1);
        visited.Should().Equal(history);
        visited.Should().NotContain(anotherResumesScore.Id);
    }

    // MORE ROWS THAN THE CEILING, deliberately, and the expected count is a LITERAL. Both halves were
    // arrived at by breaking the test on purpose rather than by reasoning about it.
    //
    // The seed: with fewer analyses stored than the ceiling, every cap from 1 upward returns the same
    // page, so the test would assert that the store contains the rows it was just given and nothing at
    // all about clamping. 101 is the smallest seed that can tell "clamped" from "handed the raw limit".
    //
    // The literal: written as HaveCount(PageRequest.MaxLimit) over a seed of MaxLimit + 1, this test
    // stays GREEN when the ceiling is changed from 100 to 50 — measured, not supposed — because both
    // sides of the comparison move together. PageRequestTests carries the same tautology
    // ([InlineData(1000, PageRequest.MaxLimit)]), so nothing in the repo pinned the number. It is
    // written out here, which makes this the one place a silent change to a public page ceiling is red.
    [Fact]
    public async Task Handle_WithALimitBeyondTheCeiling_ClampsItToOneHundred()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        for (var index = 0; index < 101; index++)
            await SeedAnalysis(resume.Id);

        var page = await Page(owner, resume.Id, limit: 100_000);

        page.Items.Should().HaveCount(100, "PageRequest.MaxLimit is the ceiling and the handler cannot bypass it");
        page.NextCursor.Should().NotBeNull("the 101st row is still there to be walked to");
    }

    [Fact]
    public async Task Handle_WithALimitBelowTheFloor_ClampsItUpwardInsteadOfFailing()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        await SeedAnalysis(resume.Id);
        await SeedAnalysis(resume.Id);

        var page = await Page(owner, resume.Id, limit: 0);

        page.Items.Should().ContainSingle();
        page.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_ForAResumeThatWasNeverScored_IsAnEmptyFinalPage()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);

        var page = await Page(owner, resume.Id);

        page.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ForAResumeThatDoesNotExist_IsNotFound()
    {
        var result = await _handler.Handle(new GetAnalysisHistoryQuery(AccountId.New(), ResumeId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Resume not found.");
    }

    [Fact]
    public async Task Handle_ForSomebodyElsesResume_IsForbidden()
    {
        var resume = await SeedResume(AccountId.New());
        await SeedAnalysis(resume.Id);

        var result = await _handler.Handle(new GetAnalysisHistoryQuery(AccountId.New(), resume.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
    }

    // THE HANDLER'S ORDER, pinned. Authorization is decided before the cursor is looked at, so a
    // stranger cannot use the difference between "Forbidden." and "Invalid cursor." to learn whether the
    // resume it guessed at exists — and nothing queries the score history of a resume it does not own.
    [Fact]
    public async Task Handle_ForSomebodyElsesResumeWithAMalformedCursor_StillAnswersForbidden()
    {
        var resume = await SeedResume(AccountId.New());
        await SeedAnalysis(resume.Id);

        var result = await _handler.Handle(
            new GetAnalysisHistoryQuery(AccountId.New(), resume.Id, 10, "nonsense"));

        result.Error.Should().Be("Forbidden.");
        _analyses.ReadCount.Should().Be(0, "a stranger's request never reaches the history it asked for");
    }

    // The other half of the order: a cursor the caller invented is rejected BEFORE anything queries the
    // store. ReadCount is the only thing that can say so — move the validation after the repository call
    // and every other assertion here stays green, leaving the test NAME as the sole claim.
    //
    // The counter lives on the ANALYSIS fake rather than the resume one, unlike
    // GetResumesByOwnerHandlerTests: this handler authorizes by reading the resume, so the resume
    // counter is already at 1 by the time the cursor is parsed and can prove nothing.
    [Theory]
    [InlineData("nonsense")]
    [InlineData("AAAAAAAAAAA")]
    [InlineData("%00%00")]
    public async Task Handle_WithACursorItCannotDecode_FailsWithoutTouchingTheHistory(string cursor)
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        await SeedAnalysis(resume.Id);

        var result = await _handler.Handle(new GetAnalysisHistoryQuery(owner, resume.Id, 10, cursor));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PageRequest.InvalidCursorError);
        result.Value.Should().BeNull();
        _analyses.ReadCount.Should().Be(0, "a forged cursor is rejected before anything queries the store");
    }

    // The companion that stops the zero above passing vacuously: a counter that never increments would
    // make "without touching the history" true for the wrong reason. Both calls carry weight — the first
    // has no cursor, the second carries one this handler minted, so the branch the assertion above
    // claims the absence of is exercised here.
    [Fact]
    public async Task Handle_WithAUsableCursor_DoesQueryTheHistory()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        await SeedAnalysis(resume.Id);
        await SeedAnalysis(resume.Id);

        var firstPage = await Page(owner, resume.Id, limit: 1);
        firstPage.NextCursor.Should().NotBeNull();

        var secondPage = await Page(owner, resume.Id, limit: 1, firstPage.NextCursor);

        secondPage.Items.Should().ContainSingle();
        _analyses.ReadCount.Should().Be(2);
    }

    private async Task<Page<Analysis>> Page(
        AccountId requester, ResumeId resumeId, int? limit = null, string? cursor = null)
    {
        var result = await _handler.Handle(
            new GetAnalysisHistoryQuery(requester, resumeId, limit, cursor));
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    private async Task<Resume> SeedResume(AccountId owner)
    {
        var resume = Resume.Create(owner, new ContactInformation(
            PersonName.Create("Jane Doe"), Email.Create($"{Guid.NewGuid():N}@example.com")));
        await _resumes.AddAsync(resume);
        return resume;
    }

    private async Task<Analysis> SeedAnalysis(ResumeId resumeId)
    {
        var analysis = Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, ScoringWeightsSnapshot.Default()),
            resumeId,
            JobPostingId.New(),
            DateTimeOffset.UtcNow);
        await _analyses.AddAsync(analysis);
        return analysis;
    }
}

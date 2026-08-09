namespace BuildCv.Application.Tests.Readability;

using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Readability;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

public sealed class GetReadabilityHistoryHandlerTests
{
    // The SAME INSTANT on every seeded report, deliberately. EvaluatedAt is supplied by the handler's
    // clock, so two runs of one resume can carry the same value — an implementation that ordered on it
    // instead of on the insertion counter would be free to answer either way round, and this seed is
    // what makes that a failure rather than a coin flip.
    private static readonly DateTimeOffset EvaluatedAt = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeResumeRepository _resumes = new();
    private readonly FakeReadabilityReportRepository _reports = new();
    private readonly GetReadabilityHistoryHandler _handler;

    public GetReadabilityHistoryHandlerTests() =>
        _handler = new GetReadabilityHistoryHandler(_resumes, _reports);

    // OLDEST FIRST, the second list in this repo that does not page newest first. Every other paged
    // assertion outside the scoring tests reads the other way round, so this is the direction a refactor
    // is most likely to "correct" — and the product breaks silently when it does: the history stops
    // being the record of whether acting on the advice paid what its measured Impact promised, and
    // becomes an unordered pile with the newest run on top.
    [Fact]
    public async Task Handle_WithNoPagingArguments_ReturnsTheResumesHistoryOldestFirst()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var first = await SeedReport(resume.Id);
        var second = await SeedReport(resume.Id);
        var third = await SeedReport(resume.Id);

        var page = await Page(owner, resume.Id);

        page.Items.Select(report => report.Id).Should().Equal(first.Id, second.Id, third.Id);
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
        var history = new List<ReadabilityReportId>();
        for (var index = 0; index < 5; index++)
            history.Add((await SeedReport(resume.Id)).Id);

        var otherResume = await SeedResume(owner);
        var anotherResumesReport = await SeedReport(otherResume.Id);

        var visited = new List<ReadabilityReportId>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await Page(owner, resume.Id, limit: 2, cursor);
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items.Select(report => report.Id));
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 2, 1);
        visited.Should().Equal(history);
        visited.Should().NotContain(anotherResumesReport.Id);
    }

    // MORE ROWS THAN THE CEILING, deliberately, and the expected count is a LITERAL — both halves for
    // the reasons GetAnalysisHistoryHandlerTests states at length. With fewer reports stored than the
    // ceiling, every cap from 1 upward returns the same page and the test asserts nothing about
    // clamping; written as HaveCount(PageRequest.MaxLimit) over a seed of MaxLimit + 1 it stays green
    // when the ceiling moves, because both sides of the comparison move together.
    //
    // What it covers is the HANDLER, which could stop routing the caller's limit through
    // PageRequest.Create at all and answer a hundred thousand rows. The constant itself is pinned by
    // PageRequestTests.
    [Fact]
    public async Task Handle_WithALimitBeyondTheCeiling_ClampsItToOneHundred()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        for (var index = 0; index < 101; index++)
            await SeedReport(resume.Id);

        var page = await Page(owner, resume.Id, limit: 100_000);

        page.Items.Should().HaveCount(100, "PageRequest.MaxLimit is the ceiling and the handler cannot bypass it");
        page.NextCursor.Should().NotBeNull("the 101st row is still there to be walked to");
    }

    [Fact]
    public async Task Handle_WithALimitBelowTheFloor_ClampsItUpwardInsteadOfFailing()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        await SeedReport(resume.Id);
        await SeedReport(resume.Id);

        var page = await Page(owner, resume.Id, limit: 0);

        page.Items.Should().ContainSingle();
        page.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_ForAResumeThatWasNeverEvaluated_IsAnEmptyFinalPage()
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
        var result = await _handler.Handle(new GetReadabilityHistoryQuery(AccountId.New(), ResumeId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Resume not found.");
    }

    [Fact]
    public async Task Handle_ForSomebodyElsesResume_IsForbidden()
    {
        var resume = await SeedResume(AccountId.New());
        await SeedReport(resume.Id);

        var result = await _handler.Handle(new GetReadabilityHistoryQuery(AccountId.New(), resume.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
    }

    // THE HANDLER'S ORDER, pinned. Authorization is decided before the cursor is looked at, so a
    // stranger cannot use the difference between "Forbidden." and "Invalid cursor." to learn whether the
    // resume it guessed at exists — and nothing queries the readability history of a resume it does not
    // own, which matters here because a report's advice quotes the candidate's own bullet points.
    [Fact]
    public async Task Handle_ForSomebodyElsesResumeWithAMalformedCursor_StillAnswersForbidden()
    {
        var resume = await SeedResume(AccountId.New());
        await SeedReport(resume.Id);

        var result = await _handler.Handle(
            new GetReadabilityHistoryQuery(AccountId.New(), resume.Id, 10, "nonsense"));

        result.Error.Should().Be("Forbidden.");
        _reports.ReadCount.Should().Be(0, "a stranger's request never reaches the history it asked for");
    }

    // The other half of the order: a cursor the caller invented is rejected BEFORE anything queries the
    // store. ReadCount is the only thing that can say so — move the validation after the repository call
    // and every other assertion here stays green, leaving the test NAME as the sole claim.
    //
    // The counter lives on the REPORT fake rather than the resume one: this handler authorizes by
    // reading the resume, so the resume counter is already at 1 by the time the cursor is parsed and can
    // prove nothing.
    [Theory]
    [InlineData("nonsense")]
    [InlineData("AAAAAAAAAAA")]
    [InlineData("%00%00")]
    public async Task Handle_WithACursorItCannotDecode_FailsWithoutTouchingTheHistory(string cursor)
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        await SeedReport(resume.Id);

        var result = await _handler.Handle(new GetReadabilityHistoryQuery(owner, resume.Id, 10, cursor));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PageRequest.InvalidCursorError);
        result.Value.Should().BeNull();
        _reports.ReadCount.Should().Be(0, "a forged cursor is rejected before anything queries the store");
    }

    // The companion that stops both zeros above passing vacuously: a counter that never increments would
    // make "without touching the history" true for the wrong reason. Both calls carry weight — the first
    // has no cursor, the second carries one this handler minted, so the branch the assertions above
    // claim the absence of is exercised here.
    [Fact]
    public async Task Handle_WithAUsableCursor_DoesQueryTheHistory()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        await SeedReport(resume.Id);
        await SeedReport(resume.Id);

        var firstPage = await Page(owner, resume.Id, limit: 1);
        firstPage.NextCursor.Should().NotBeNull();

        var secondPage = await Page(owner, resume.Id, limit: 1, firstPage.NextCursor);

        secondPage.Items.Should().ContainSingle();
        _reports.ReadCount.Should().Be(2);
    }

    private async Task<Page<ReadabilityReport>> Page(
        AccountId requester, ResumeId resumeId, int? limit = null, string? cursor = null)
    {
        var result = await _handler.Handle(
            new GetReadabilityHistoryQuery(requester, resumeId, limit, cursor));
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

    private async Task<ReadabilityReport> SeedReport(ResumeId resumeId)
    {
        var report = ReadabilityReport.Create(
            ReadabilityReportId.New(),
            ReadabilityBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.0, ReadabilityWeightsSnapshot.Default()),
            resumeId,
            EvaluatedAt,
            [
                ReadabilityRecommendation.Create(
                    ReadabilitySectionType.Contact, RecommendationPriority.Important,
                    ReadabilityRecommendationKind.NoPhoneNumber, "Add a phone number.", 0.05),
            ]);

        await _reports.AddAsync(report);
        return report;
    }
}

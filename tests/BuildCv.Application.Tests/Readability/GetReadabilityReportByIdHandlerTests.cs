namespace BuildCv.Application.Tests.Readability;

using BuildCv.Application.Readability;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

public sealed class GetReadabilityReportByIdHandlerTests
{
    private readonly FakeReadabilityReportRepository _reports = new();
    private readonly FakeResumeRepository _resumes = new();
    private readonly GetReadabilityReportByIdHandler _handler;

    public GetReadabilityReportByIdHandlerTests() =>
        _handler = new GetReadabilityReportByIdHandler(_reports, _resumes);

    [Fact]
    public async Task Handle_ForTheOwnersOwnReport_ReturnsItWithItsAdvice()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var report = await SeedReport(resume.Id);

        var result = await _handler.Handle(new GetReadabilityReportByIdQuery(owner, report.Id));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be(report.Id);

        // The advice travels with the numbers. A read that returned the breakdown and dropped the
        // recommendations would satisfy every assertion about the score and lose the half a candidate
        // acts on — and the Impact on each one is only meaningful beside the breakdown it was measured
        // against, which is why the two are stored in one row.
        result.Value.Recommendations.Should().ContainSingle()
            .Which.Kind.Should().Be(ReadabilityRecommendationKind.NoPhoneNumber);
    }

    // "not found.", with the trailing period, because ResultExtensions routes on that exact suffix. A
    // message ending any other way becomes a 400, which tells a client its request was malformed when
    // the request was fine and the row simply is not there.
    [Fact]
    public async Task Handle_ForAReportThatWasNeverStored_IsNotFound()
    {
        var result = await _handler.Handle(
            new GetReadabilityReportByIdQuery(AccountId.New(), ReadabilityReportId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Readability report not found.");
        result.Value.Should().BeNull();
    }

    // A ReadabilityReport carries no owner, so this is the assertion that the SECOND read actually
    // happens and actually gates. Delete the ownership check and a stranger reads advice quoting
    // somebody else's job titles and employment gaps back at them.
    [Fact]
    public async Task Handle_ForSomebodyElsesReport_IsForbidden()
    {
        var resume = await SeedResume(AccountId.New());
        var report = await SeedReport(resume.Id);

        var result = await _handler.Handle(
            new GetReadabilityReportByIdQuery(AccountId.New(), report.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
        result.Value.Should().BeNull();
    }

    // The orphan. NO SHIPPED STORE PRODUCES ONE, and the branch stays anyway — the same ruling
    // GetAnalysisByIdHandlerTests makes, reached the same way.
    //
    // Under EF it never could: ResumeRepository.CascadeToReadabilityReportsAsync tombstones the reports
    // in the same unit of work, so the miss happens on the first read. The in-memory store now drops
    // them in InMemoryResumeRepository.DeleteAsync. What is left is a state only this fake can reach,
    // because FakeResumeRepository.DeleteAsync removes the resume and nothing else.
    //
    // That is the point rather than an accident. The branch is what would keep the two providers
    // agreeing if either cascade were removed or a third store added, and it is free: it must answer
    // "Readability report not found." because the caller named a report, and telling it about a resume
    // it does not own would answer a question it did not ask.
    [Fact]
    public async Task Handle_WhenTheResumeBehindItIsGone_IsNotFoundRatherThanForbiddenOrLeaked()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var report = await SeedReport(resume.Id);
        await _resumes.DeleteAsync(resume.Id);

        var result = await _handler.Handle(new GetReadabilityReportByIdQuery(owner, report.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Readability report not found.");
        result.Value.Should().BeNull("advice outliving the CV it was written about must not be readable");
    }

    // EDITING THE CV DOES NOT MOVE A STORED REPORT, and there is no staleness flag to raise. Unlike
    // Analysis, ReadabilityReport records nothing about the resume's state at evaluation time — there is
    // no counterpart to Analysis.ResumeUpdatedAt — so a report is returned as it was taken, and the way
    // to find out where the CV stands now is to POST for a new one.
    //
    // Asserted rather than assumed, because "the read path re-evaluates" is the plausible-looking
    // behaviour a reader might add: it would silently rewrite the candidate's history.
    [Fact]
    public async Task Handle_AfterTheResumeWasEdited_StillReturnsTheReportExactlyAsItWasTaken()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var report = await SeedReport(resume.Id);

        var before = resume.UpdatedAt;
        resume.AddSkill(Skill.Create(Technology.Create("SQL")));
        resume.UpdatedAt.Should().NotBe(before, "or the edit did not move the timestamp a re-read would see");

        var result = await _handler.Handle(new GetReadabilityReportByIdQuery(owner, report.Id));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Breakdown.Should().Be(report.Breakdown);
        result.Value.EvaluatedAt.Should().Be(report.EvaluatedAt);
        result.Value.Recommendations.Should().Equal(report.Recommendations);
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
            DateTimeOffset.UtcNow,
            [
                ReadabilityRecommendation.Create(
                    ReadabilitySectionType.Contact, RecommendationPriority.Important,
                    ReadabilityRecommendationKind.NoPhoneNumber, "Add a phone number.", 0.05),
            ]);

        await _reports.AddAsync(report);
        return report;
    }
}

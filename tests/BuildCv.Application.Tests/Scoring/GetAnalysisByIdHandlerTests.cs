namespace BuildCv.Application.Tests.Scoring;

using BuildCv.Application.Scoring;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

public sealed class GetAnalysisByIdHandlerTests
{
    private readonly FakeAnalysisRepository _analyses = new();
    private readonly FakeResumeRepository _resumes = new();
    private readonly GetAnalysisByIdHandler _handler;

    public GetAnalysisByIdHandlerTests() => _handler = new GetAnalysisByIdHandler(_analyses, _resumes);

    [Fact]
    public async Task Handle_ForTheOwnersOwnAnalysis_ReturnsIt()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var analysis = await SeedAnalysis(resume.Id);

        var result = await _handler.Handle(new GetAnalysisByIdQuery(owner, analysis.Id));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Analysis.Id.Should().Be(analysis.Id);
    }

    // "not found.", with the trailing period, because ResultExtensions routes on that exact suffix. A
    // message ending any other way becomes a 400, which tells a client its request was malformed when
    // the request was fine and the row simply is not there.
    [Fact]
    public async Task Handle_ForAnAnalysisThatWasNeverStored_IsNotFound()
    {
        var result = await _handler.Handle(new GetAnalysisByIdQuery(AccountId.New(), AnalysisId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Analysis not found.");
        result.Value.Should().BeNull();
    }

    // An Analysis carries no owner, so this is the assertion that the SECOND read actually happens and
    // actually gates. Delete the ownership check and the stranger reads a full breakdown of somebody
    // else's CV against somebody else's posting.
    [Fact]
    public async Task Handle_ForSomebodyElsesAnalysis_IsForbidden()
    {
        var resume = await SeedResume(AccountId.New());
        var analysis = await SeedAnalysis(resume.Id);

        var result = await _handler.Handle(new GetAnalysisByIdQuery(AccountId.New(), analysis.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
        result.Value.Should().BeNull();
    }

    // The orphan, and it is what keeps the two persistence providers telling the same story.
    //
    // Under EF this state cannot arise: ResumeRepository.DeleteAsync tombstones the analyses in the same
    // unit of work, so the miss happens on the first read. The in-memory store — the one every Api test
    // runs on — has no cascade, so the analysis outlives its resume and arrives at the second read. Both
    // must answer the same thing, and it must be "Analysis not found.": the caller named an analysis,
    // and telling it about a resume it does not own would answer a question it did not ask.
    [Fact]
    public async Task Handle_WhenTheResumeBehindItIsGone_IsNotFoundRatherThanForbiddenOrLeaked()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var analysis = await SeedAnalysis(resume.Id);
        await _resumes.DeleteAsync(resume.Id);

        var result = await _handler.Handle(new GetAnalysisByIdQuery(owner, analysis.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Analysis not found.");
        result.Value.Should().BeNull("a score outliving the CV it was computed from must not be readable");
    }

    // STALENESS, the three states a candidate can be in. It is decided here, on a read, and stored
    // nowhere: the analysis row is unchanged in all three, and only the resume beside it differs.
    [Fact]
    public async Task Handle_WhenTheResumeHasNotChangedSinceTheScore_IsNotStale()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var analysis = await SeedAnalysis(resume.Id, resume.UpdatedAt);

        var result = await _handler.Handle(new GetAnalysisByIdQuery(owner, analysis.Id));

        result.Value!.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_AfterTheResumeWasEdited_IsStale()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var analysis = await SeedAnalysis(resume.Id, resume.UpdatedAt);

        var before = resume.UpdatedAt;
        resume.AddSkill(Skill.Create(Technology.Create("SQL")));
        resume.UpdatedAt.Should().NotBe(before, "or the edit did not move the timestamp this reads");

        var result = await _handler.Handle(new GetAnalysisByIdQuery(owner, analysis.Id));

        result.Value!.IsStale.Should().BeTrue(
            "the score describes a CV the candidate no longer has");
    }

    // UNKNOWN IS STALE, NEVER FRESH. This is what every analysis written before the provenance columns
    // existed reads back as, and it is the direction that matters: the alternative reports a score taken
    // against a CV nobody can identify as current, which is the one wrong answer that misleads rather
    // than merely costs a re-score.
    [Fact]
    public async Task Handle_ForAnAnalysisWithNoRecordedProvenance_IsStale()
    {
        var owner = AccountId.New();
        var resume = await SeedResume(owner);
        var analysis = await SeedAnalysis(resume.Id, resumeUpdatedAt: null);

        var result = await _handler.Handle(new GetAnalysisByIdQuery(owner, analysis.Id));

        result.Value!.Analysis.ResumeUpdatedAt.Should().BeNull("or this is not the historical case");
        result.Value.IsStale.Should().BeTrue();
    }

    private async Task<Resume> SeedResume(AccountId owner)
    {
        var resume = Resume.Create(owner, new ContactInformation(
            PersonName.Create("Jane Doe"), Email.Create($"{Guid.NewGuid():N}@example.com")));
        await _resumes.AddAsync(resume);
        return resume;
    }

    private async Task<Analysis> SeedAnalysis(ResumeId resumeId, DateTimeOffset? resumeUpdatedAt = null)
    {
        var analysis = Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, ScoringWeightsSnapshot.Default()),
            resumeId,
            JobPostingId.New(),
            DateTimeOffset.UtcNow,
            recommendations: null,
            resumeUpdatedAt);
        await _analyses.AddAsync(analysis);
        return analysis;
    }
}

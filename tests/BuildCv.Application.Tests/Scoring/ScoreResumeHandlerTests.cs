using BuildCv.Application.Scoring;
using BuildCv.Application.Tests.Common.Pagination;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Scoring;

public class ScoreResumeHandlerTests
{
    private readonly FakeResumeRepository _resumes = new();
    private readonly FakeJobPostingRepository _jobPostings = new();
    private readonly FakeAnalysisRepository _analyses = new();
    private readonly ScoringEngine _scoringEngine = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ScoreResumeHandler _handler;

    public ScoreResumeHandlerTests() =>
        _handler = new ScoreResumeHandler(_resumes, _jobPostings, _analyses, _scoringEngine, _time);

    private static Resume BuildResume(AccountId ownerId, params string[] skillNames)
    {
        var contact = new ContactInformation(PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
        var resume = Resume.Create(ownerId, contact);
        foreach (var name in skillNames)
            resume.AddSkill(Skill.Create(Technology.Create(name)));
        return resume;
    }

    private static void AddProfessionalExperience(Resume resume, DateOnly start)
    {
        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Acme"),
            "Backend Developer",
            DateRange.Create(start)));
    }

    private static JobPosting BuildJobPosting(AccountId ownerId, params string[] mustHaveSkills)
    {
        var jobPosting = JobPosting.Create(ownerId, "Backend Developer", OrganizationName.Create("Acme"));
        foreach (var skill in mustHaveSkills)
            jobPosting.AddRequirement(JobRequirement.Create(Technology.Create(skill), RequirementPriority.MustHave));
        return jobPosting;
    }

    [Fact]
    public async Task Score_perfect_match_returns_high_overall_score()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId, "C#", "dotnet");
        AddProfessionalExperience(resume, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-6));
        await _resumes.AddAsync(resume);
        var jobPosting = BuildJobPosting(AccountId.New(), "C#", "dotnet");
        await _jobPostings.AddAsync(jobPosting);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Breakdown.SkillsScore.Should().Be(1.0);
        result.Value.Breakdown.ExperienceScore.Should().Be(1.0);
        result.Value.OverallScore.Should().BeGreaterThanOrEqualTo(60);
        (await _analyses.GetPageByResumeIdAsync(resume.Id, PageRequests.Of())).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Score_resume_without_matching_skills_scores_lower()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId);
        AddProfessionalExperience(resume, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-6));
        await _resumes.AddAsync(resume);
        var jobPosting = BuildJobPosting(AccountId.New(), "C#", "dotnet");
        await _jobPostings.AddAsync(jobPosting);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Breakdown.SkillsScore.Should().Be(0.0);
        result.Value.OverallScore.Should().BeLessThan(60);
    }

    [Fact]
    public async Task Score_forbidden_when_requester_is_not_owner()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId, "C#");
        await _resumes.AddAsync(resume);
        var jobPosting = BuildJobPosting(AccountId.New(), "C#");
        await _jobPostings.AddAsync(jobPosting);

        var result = await _handler.Handle(new ScoreResumeCommand(AccountId.New(), resume.Id, jobPosting.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
    }

    [Fact]
    public async Task Score_resume_not_found_fails()
    {
        var result = await _handler.Handle(new ScoreResumeCommand(AccountId.New(), ResumeId.New(), JobPostingId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Resume not found.");
    }

    [Fact]
    public async Task Score_job_posting_not_found_fails()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId, "C#");
        await _resumes.AddAsync(resume);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, JobPostingId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Job posting not found.");
    }
}

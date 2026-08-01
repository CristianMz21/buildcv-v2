using BuildCv.Application.Scoring;
using BuildCv.Application.Tests.Common.Pagination;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
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

    // Published by default. Scoring is published-or-owned, so a draft built by a stranger is the
    // FORBIDDEN case and every test that is not about visibility has to opt out of it explicitly —
    // otherwise the assertion under test never runs. Both score tests here used to build exactly that
    // stranger's draft and get a 200, which is the hole the check closes.
    private static JobPosting BuildJobPosting(AccountId ownerId, params string[] mustHaveSkills)
    {
        var jobPosting = BuildDraftJobPosting(ownerId, mustHaveSkills);
        jobPosting.Publish();
        return jobPosting;
    }

    private static JobPosting BuildDraftJobPosting(AccountId ownerId, params string[] mustHaveSkills)
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

        // 0.45*1.0 (skills) + 0.20*1.0 (experience) + 0.10*0.5 (languages: the posting states no
        // language requirement, so the section is neutral) = 0.70. Nothing else on this resume scores.
        result.Value.OverallScore.Should().Be(70);
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

        // 0.20*1.0 (experience) + 0.10*0.5 (neutral languages) = 0.25.
        result.Value.OverallScore.Should().Be(25);
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

    // The advice is persisted with the breakdown it was derived from, not recomputed on read. An Impact
    // is only meaningful beside the score it was measured against, so a history entry holding the number
    // without the advice could never answer "what was I told to do, and did it work".
    [Fact]
    public async Task Score_persists_the_recommendations_alongside_the_breakdown()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId);
        await _resumes.AddAsync(resume);
        var jobPosting = BuildJobPosting(AccountId.New(), "C#");
        await _jobPostings.AddAsync(jobPosting);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Recommendations.Should().NotBeEmpty();

        var stored = (await _analyses.GetPageByResumeIdAsync(resume.Id, PageRequests.Of())).Items
            .Should().ContainSingle().Subject;
        stored.Recommendations.Should().Equal(result.Value.Recommendations);
        stored.Recommendations.Should().BeInAscendingOrder(RecommendationOrder.Display,
            "the ten that survive the cap are chosen by this order, so the stored set is already in it");
    }

    // The three corners of published-or-owned. The first is the normal candidate flow; the second is
    // what "bring your own job offer" will look like once a candidate creates the posting themselves;
    // the third is the hole this check closes.
    [Fact]
    public async Task Score_against_a_published_posting_owned_by_someone_else_succeeds()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId, "C#");
        await _resumes.AddAsync(resume);
        var jobPosting = BuildJobPosting(AccountId.New(), "C#");
        await _jobPostings.AddAsync(jobPosting);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Score_against_an_unpublished_posting_the_requester_owns_succeeds()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId, "C#");
        await _resumes.AddAsync(resume);
        var jobPosting = BuildDraftJobPosting(ownerId, "C#");
        await _jobPostings.AddAsync(jobPosting);

        jobPosting.Status.Should().Be(JobPostingStatus.Draft, "or this asserts nothing about ownership");

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Score_against_an_unpublished_posting_owned_by_someone_else_is_forbidden()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId, "C#");
        await _resumes.AddAsync(resume);
        var jobPosting = BuildDraftJobPosting(AccountId.New(), "C#");
        await _jobPostings.AddAsync(jobPosting);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
        (await _analyses.GetPageByResumeIdAsync(resume.Id, PageRequests.Of())).Items.Should().BeEmpty(
            "a refused score must not leave an analysis behind");
    }

    // Closed and Archived are both != Published, so archiving is a scoring kill switch for everyone but
    // the owner. Pinned because it is a consequence of the check rather than something it was written
    // for, and a reader would otherwise have to derive it from an inequality.
    [Theory]
    [InlineData(JobPostingStatus.Closed)]
    [InlineData(JobPostingStatus.Archived)]
    public async Task Score_against_a_posting_that_left_published_is_forbidden_for_a_non_owner(
        JobPostingStatus status)
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId, "C#");
        await _resumes.AddAsync(resume);
        var jobPosting = BuildJobPosting(AccountId.New(), "C#");
        if (status == JobPostingStatus.Closed)
            jobPosting.Close();
        else
            jobPosting.Archive();
        await _jobPostings.AddAsync(jobPosting);

        jobPosting.Status.Should().Be(status);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        result.Error.Should().Be("Forbidden.");
    }

    // 404 before 403: a posting that does not exist is reported as missing rather than as forbidden,
    // matching GetJobPostingHandler so the two endpoints leak the same bit of existence information
    // rather than disagreeing about it.
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

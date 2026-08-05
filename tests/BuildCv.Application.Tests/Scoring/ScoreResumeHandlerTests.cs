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

        // The posting states no language requirement, so Languages is renormalized out and the other
        // five are scored out of 0.90: Skills 0.45/0.90 = 0.50, Experience 0.20/0.90 = 0.2222.
        // 0.50*1.0 + 0.2222*1.0 = 0.65/0.90 = 0.7222 -> 72. Nothing else on this resume scores.
        result.Value.OverallScore.Should().Be(72);
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

        // Experience alone, renormalized: 0.20/0.90 = 0.2222 -> 22. The unmatched skills section still
        // carries its full renormalized 0.50 and scores zero against it, which is what a candidate who
        // matches nothing the posting asked for should see.
        result.Value.OverallScore.Should().Be(22);
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

    // THE PERSISTED SNAPSHOT IS THE ONE THE SCORE WAS COMPUTED UNDER, not the defaults.
    //
    // This is the half of renormalization that must not be got wrong. ScoreBreakdown.Weights is what
    // makes a past analysis self-explaining and arithmetically reproducible — nothing on a read path
    // consults default or organization-level weights, so the row is the only record of how its own
    // number was reached. Renormalizing at score time and persisting Default() would leave every
    // historical analysis holding six weights that do not explain its own total.
    //
    // Asserted by REPRODUCING the total from the stored parts rather than by comparing to a literal: if
    // the stored weights were the wrong set, the reconstruction would miss.
    [Fact]
    public async Task Score_persists_the_weights_it_actually_scored_under()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId, "C#");
        await _resumes.AddAsync(resume);
        var jobPosting = BuildJobPosting(AccountId.New(), "C#");
        await _jobPostings.AddAsync(jobPosting);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        var stored = (await _analyses.GetPageByResumeIdAsync(resume.Id, PageRequests.Of())).Items
            .Should().ContainSingle().Subject;
        var weights = stored.Breakdown.Weights;

        weights.Should().NotBe(ScoringWeightsSnapshot.Default(),
            "this posting states no language requirement, so it was NOT scored under the defaults");
        weights.Languages.Should().Be(0.0);
        weights.Skills.Should().BeApproximately(0.45 / 0.90, 1e-9);

        Enum.GetValues<SectionType>().Sum(weights.WeightFor).Should().BeApproximately(1.0, 0.0001);

        Enum.GetValues<SectionType>()
            .Sum(section => weights.WeightFor(section) * stored.Breakdown.ScoreFor(section))
            .Should().BeApproximately(stored.Breakdown.WeightedTotal, 1e-9,
                "the stored weights and the stored scores must reproduce the stored total");

        result.Value!.Breakdown.Weights.Should().Be(weights, "and the caller was shown the same set");
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

    // ONE CLOCK SNAPSHOT PER RUN, and the two reads it replaced were a real bug rather than a style
    // point. referenceDate feeds ProfessionalDays, UnmarkedExperienceDays and ValidCertificateCount, so a
    // request straddling midnight scored against one day and stamped ScoredAt with the next: the row said
    // it was taken on a day it was not taken against, and everything reading the two together inherited
    // that.
    //
    // The clock starts 100ms before midnight and moves 200ms per read, so read one falls on 2026-08-04
    // and read two on 2026-08-05. That is what makes a second GetUtcNow() observable as a DATE
    // difference; two reads a millisecond apart mid-afternoon would agree by luck and prove nothing.
    //
    // The referenceDate is read off the ENGINE rather than re-derived from the clock. Re-deriving it here
    // would repeat the handler's own arithmetic and agree with it whatever it did; the recorder reports
    // the value the engine was really handed.
    [Fact]
    public async Task Score_across_midnight_stamps_the_analysis_with_the_day_it_scored_against()
    {
        var clock = new AdvancingTimeProvider(
            new DateTimeOffset(2026, 8, 4, 23, 59, 59, 900, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(200));
        var engine = new RecordingScoringEngine(new ScoringEngine());
        var handler = new ScoreResumeHandler(_resumes, _jobPostings, _analyses, engine, clock);

        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId, "C#");
        AddProfessionalExperience(resume, new DateOnly(2020, 1, 1));
        await _resumes.AddAsync(resume);
        var jobPosting = BuildJobPosting(AccountId.New(), "C#");
        await _jobPostings.AddAsync(jobPosting);

        var result = await handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        result.IsSuccess.Should().BeTrue();
        clock.ReadCount.Should().Be(1, "one scoring run takes one snapshot of the clock");

        var referenceDate = engine.ReferenceDates.Should().ContainSingle().Subject;
        referenceDate.Should().Be(new DateOnly(2026, 8, 4), "the first read is still yesterday");
        DateOnly.FromDateTime(result.Value!.ScoredAt.UtcDateTime).Should().Be(referenceDate,
            "an analysis must be stamped with the day it was actually scored against");
    }

    // DE-DUPLICATION. Four tests below, one per term of the key, because a test that only re-posts and
    // checks the response cannot see the difference: a reused analysis and a freshly written duplicate
    // answer with the same numbers. FakeAnalysisRepository.WriteCount is the assertion that can.
    [Fact]
    public async Task Score_twice_with_nothing_changed_writes_one_row_and_returns_the_stored_one()
    {
        var ownerId = AccountId.New();
        var (resume, jobPosting) = await ArrangeScorableAsync(ownerId);
        var command = new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id);

        var first = await _handler.Handle(command);
        var second = await _handler.Handle(command);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        _analyses.WriteCount.Should().Be(1, "the second run is the same score, not a second scoring event");
        second.Value!.Id.Should().Be(first.Value!.Id, "and the caller is handed the row that already exists");
        (await _analyses.GetPageByResumeIdAsync(resume.Id, PageRequests.Of())).Items
            .Should().ContainSingle("a history of button presses is not a history");
    }

    // TERM 1 — the resume version. Adding a skill calls Touch(), which moves Resume.UpdatedAt.
    [Fact]
    public async Task Score_after_the_resume_changed_writes_a_second_row()
    {
        var ownerId = AccountId.New();
        var (resume, jobPosting) = await ArrangeScorableAsync(ownerId);
        var command = new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id);

        var first = await _handler.Handle(command);
        var before = resume.UpdatedAt;
        resume.AddSkill(Skill.Create(Technology.Create("SQL")));
        resume.UpdatedAt.Should().NotBe(before, "or this test would prove nothing about the resume term");

        var second = await _handler.Handle(command);

        _analyses.WriteCount.Should().Be(2, "the score now describes a different CV");
        second.Value!.Id.Should().NotBe(first.Value!.Id);
    }

    // TERM 2 — the posting version. A recruiter adding a requirement changes the skills score of every
    // candidate already scored against it, so their stored score no longer describes this posting.
    [Fact]
    public async Task Score_after_the_job_posting_changed_writes_a_second_row()
    {
        var ownerId = AccountId.New();
        var (resume, jobPosting) = await ArrangeScorableAsync(ownerId);
        var command = new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id);

        var first = await _handler.Handle(command);
        var before = jobPosting.UpdatedAt;
        jobPosting.AddRequirement(JobRequirement.Create(
            Technology.Create("Kubernetes"), RequirementPriority.NiceToHave));
        jobPosting.UpdatedAt.Should().NotBe(before, "or this test would prove nothing about the posting term");

        var second = await _handler.Handle(command);

        _analyses.WriteCount.Should().Be(2, "the score now describes a different posting");
        second.Value!.Id.Should().NotBe(first.Value!.Id);
    }

    // TERM 4 — THE DATE, and it is the term that looks droppable and is not.
    //
    // Scoring is deterministic given (resume, posting, weights, REFERENCE DATE) and not given
    // (resume, posting): referenceDate feeds ProfessionalDays, UnmarkedExperienceDays and
    // ValidCertificateCount, so this candidate's professional experience is one day longer tomorrow and a
    // certificate can expire overnight. A key without the date would pin a candidate's score to the first
    // day they ever ran it and never move again.
    //
    // The clock is advanced by exactly one day rather than to a chosen hour, so the date changes whatever
    // instant the fixture seeded — a fixed target time would only cross midnight for some seeds.
    [Fact]
    public async Task Score_on_the_next_day_writes_a_second_row()
    {
        var ownerId = AccountId.New();
        var (resume, jobPosting) = await ArrangeScorableAsync(ownerId);
        var command = new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id);

        var first = await _handler.Handle(command);
        var firstDay = DateOnly.FromDateTime(first.Value!.ScoredAt.UtcDateTime);

        _time.Advance(TimeSpan.FromDays(1));
        var second = await _handler.Handle(command);

        DateOnly.FromDateTime(second.Value!.ScoredAt.UtcDateTime).Should().NotBe(firstDay,
            "or the clock did not actually cross a day boundary and the rest asserts nothing");
        _analyses.WriteCount.Should().Be(2, "the same CV against the same posting scores differently today");
        second.Value.Id.Should().NotBe(first.Value.Id);
    }

    // TERM 3 — the scoring model. The stored row matches on every other term; only its SchemaVersion is
    // behind, which is what a lexicon revision or a changed cap looks like from here. Nothing in the
    // shipped API can produce such a row, so it is seeded directly — the alternative is bumping
    // CurrentSchemaVersion in a test, which would restamp every other assertion in the suite.
    [Fact]
    public async Task Score_when_the_stored_analysis_predates_the_current_model_writes_a_second_row()
    {
        var ownerId = AccountId.New();
        var (resume, jobPosting) = await ArrangeScorableAsync(ownerId);

        var stale = StoredAnalysis(
            resume, jobPosting, _time.GetUtcNow(),
            resume.UpdatedAt, jobPosting.UpdatedAt,
            ScoringWeightsSnapshot.CurrentSchemaVersion - 1);
        await _analyses.AddAsync(stale);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        _analyses.WriteCount.Should().Be(2, "a score explained by a retired model is not this score");
        result.Value!.Id.Should().NotBe(stale.Id);
        result.Value.Breakdown.Weights.SchemaVersion.Should().Be(ScoringWeightsSnapshot.CurrentSchemaVersion);
    }

    // THE HISTORICAL ROW. Both provenance columns are null — what every analysis written before those
    // columns existed reads back as — and null means UNKNOWN, never "unchanged". Reusing it would tell a
    // candidate a score taken against a CV nobody can identify is current.
    [Fact]
    public async Task Score_when_the_stored_analysis_has_unknown_provenance_writes_a_second_row()
    {
        var ownerId = AccountId.New();
        var (resume, jobPosting) = await ArrangeScorableAsync(ownerId);

        var historical = StoredAnalysis(
            resume, jobPosting, _time.GetUtcNow(),
            resumeUpdatedAt: null, jobPostingUpdatedAt: null,
            ScoringWeightsSnapshot.CurrentSchemaVersion);
        await _analyses.AddAsync(historical);

        var result = await _handler.Handle(new ScoreResumeCommand(ownerId, resume.Id, jobPosting.Id));

        _analyses.WriteCount.Should().Be(2, "unknown provenance is stale, not fresh");
        result.Value!.Id.Should().NotBe(historical.Id);
    }

    private async Task<(Resume Resume, JobPosting JobPosting)> ArrangeScorableAsync(AccountId ownerId)
    {
        var resume = BuildResume(ownerId, "C#");
        AddProfessionalExperience(resume, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-6));
        await _resumes.AddAsync(resume);

        var jobPosting = BuildJobPosting(AccountId.New(), "C#");
        await _jobPostings.AddAsync(jobPosting);

        return (resume, jobPosting);
    }

    // A row already in the store, built to match the de-duplication key on every term except the one the
    // caller varies. The breakdown's values are irrelevant — nothing compares them — so they are constant.
    private static Analysis StoredAnalysis(
        Resume resume,
        JobPosting jobPosting,
        DateTimeOffset scoredAt,
        DateTimeOffset? resumeUpdatedAt,
        DateTimeOffset? jobPostingUpdatedAt,
        int schemaVersion) =>
        Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(
                0.5, 0.5, 0.5, 0.5, 0.5, 0.5,
                ScoringWeightsSnapshot.Create(0.45, 0.20, 0.10, 0.10, 0.05, 0.10, schemaVersion)),
            resume.Id,
            jobPosting.Id,
            scoredAt,
            recommendations: null,
            resumeUpdatedAt,
            jobPostingUpdatedAt);

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

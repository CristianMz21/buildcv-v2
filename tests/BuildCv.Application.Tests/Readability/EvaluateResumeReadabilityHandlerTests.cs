using BuildCv.Application.Readability;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Readability;

// THE DEPENDENCY LIST IS THE FEATURE. This handler is constructed from a resume repository, a report
// repository, the engine and a clock — there is no IJobPostingRepository to hand it, so a readability
// run cannot depend on a posting existing anywhere in the system.
public class EvaluateResumeReadabilityHandlerTests
{
    private readonly FakeResumeRepository _resumes = new();
    private readonly FakeReadabilityReportRepository _reports = new();
    private readonly ReadabilityEngine _engine = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly EvaluateResumeReadabilityHandler _handler;

    public EvaluateResumeReadabilityHandlerTests() =>
        _handler = new EvaluateResumeReadabilityHandler(_resumes, _reports, _engine, _time);

    [Fact]
    public async Task Evaluate_returns_a_report_naming_the_resume_and_the_instant_it_was_taken()
    {
        var ownerId = AccountId.New();
        var resume = ReadabilityTestResumes.FullyPopulated();
        var owned = Owned(resume, ownerId);
        await _resumes.AddAsync(owned);

        var result = await _handler.Handle(new EvaluateResumeReadabilityCommand(ownerId, owned.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value!.ResumeId.Should().Be(owned.Id);
        result.Value.EvaluatedAt.Should().Be(_time.GetUtcNow());
        result.Value.ReadabilityScore.Should().Be(100);
        result.Value.Band.Should().Be(ReadabilityBand.Strong);
    }

    // "The result says success" and "a row was written" are different claims, and only the counter is
    // about the store: a handler that returned a report it never persisted would pass every assertion
    // about the response.
    [Fact]
    public async Task Evaluate_writes_exactly_one_report()
    {
        var ownerId = AccountId.New();
        var resume = Owned(ReadabilityTestResumes.Empty(), ownerId);
        await _resumes.AddAsync(resume);

        await _handler.Handle(new EvaluateResumeReadabilityCommand(ownerId, resume.Id));

        _reports.WriteCount.Should().Be(1);
        _reports.Reports.Should().ContainSingle(report => report.ResumeId == resume.Id);
    }

    // The advice is persisted alongside the breakdown it was derived from. It has to be: an Impact is
    // only meaningful next to the score it was measured against.
    [Fact]
    public async Task Evaluate_persists_the_advice_beside_the_breakdown_it_was_derived_from()
    {
        var ownerId = AccountId.New();
        var resume = Owned(ReadabilityTestResumes.Empty(), ownerId);
        await _resumes.AddAsync(resume);

        await _handler.Handle(new EvaluateResumeReadabilityCommand(ownerId, resume.Id));

        var stored = _reports.Reports.Single();
        stored.Recommendations.Should().NotBeEmpty();
        stored.Breakdown.WeightedTotal.Should().Be(0.0);
    }

    [Fact]
    public async Task Evaluate_returns_not_found_for_a_resume_that_does_not_exist()
    {
        var result = await _handler.Handle(
            new EvaluateResumeReadabilityCommand(AccountId.New(), ResumeId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Resume not found.");
        _reports.WriteCount.Should().Be(0);
    }

    // Owner only. The advice quotes the candidate's resume back at them, so this is not the aggregate to
    // widen by reflex — and the store must stay untouched, which "the result says Forbidden" alone
    // cannot say.
    [Fact]
    public async Task Evaluate_returns_forbidden_for_someone_elses_resume_and_writes_nothing()
    {
        var resume = Owned(ReadabilityTestResumes.FullyPopulated(), AccountId.New());
        await _resumes.AddAsync(resume);

        var result = await _handler.Handle(
            new EvaluateResumeReadabilityCommand(AccountId.New(), resume.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
        _reports.WriteCount.Should().Be(0);
    }

    // ONE CLOCK READ for the whole run. referenceDate feeds the employment-gap walk, so a request that
    // straddled midnight would measure the timeline against yesterday and stamp EvaluatedAt with today,
    // leaving a row that contradicts itself. Counted rather than inferred from the two dates agreeing —
    // two reads inside the same second would agree by luck.
    [Fact]
    public async Task Evaluate_reads_the_clock_once_so_the_row_cannot_contradict_itself()
    {
        var ownerId = AccountId.New();
        var resume = Owned(ReadabilityTestResumes.Empty(), ownerId);
        await _resumes.AddAsync(resume);

        // Seeded one minute before midnight with a two-minute step, so a second read would land on the
        // next DATE and not merely on a later instant.
        var clock = new AdvancingTimeProvider(
            new DateTimeOffset(2025, 3, 10, 23, 59, 0, TimeSpan.Zero), TimeSpan.FromMinutes(2));
        var handler = new EvaluateResumeReadabilityHandler(_resumes, _reports, _engine, clock);

        var result = await handler.Handle(new EvaluateResumeReadabilityCommand(ownerId, resume.Id));

        result.IsSuccess.Should().BeTrue();
        clock.ReadCount.Should().Be(1);
        result.Value!.EvaluatedAt.Should().Be(new DateTimeOffset(2025, 3, 10, 23, 59, 0, TimeSpan.Zero));
    }

    // A resume is created with an owner, and the test resumes mint their own — so an ownership scenario
    // has to rebuild the aggregate under the account it is about.
    private static Resume Owned(Resume source, AccountId ownerId)
    {
        var resume = Resume.Create(ownerId, source.ContactInformation);
        foreach (var experience in source.Experiences)
            resume.AddExperience(experience);
        foreach (var education in source.Educations)
            resume.AddEducation(education);
        foreach (var skill in source.Skills)
            resume.AddSkill(skill);
        return resume;
    }
}

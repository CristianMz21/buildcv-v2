using BuildCv.Application.Scoring;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Scoring;

public class ScoringEngineTests
{
    private static readonly DateOnly ReferenceDate = new(2025, 1, 1);

    private readonly ScoringEngine _engine = new();

    private static Resume BuildResume(params string[] skillNames)
    {
        var contact = new ContactInformation(PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
        var resume = Resume.Create(AccountId.New(), contact);
        foreach (var name in skillNames)
            resume.AddSkill(Skill.Create(Technology.Create(name)));
        return resume;
    }

    private static JobPosting BuildJobPosting(params (string Skill, RequirementPriority Priority)[] requirements)
    {
        var jobPosting = JobPosting.Create(AccountId.New(), "Backend Developer", OrganizationName.Create("Acme"));
        foreach (var (skill, priority) in requirements)
            jobPosting.AddRequirement(JobRequirement.Create(Technology.Create(skill), priority));
        return jobPosting;
    }

    [Fact]
    public void Skills_no_requirements_returns_neutral()
    {
        var result = _engine.Score(BuildResume("C#"), BuildJobPosting(), ReferenceDate);

        result.SkillsScore.Should().Be(0.5);
    }

    [Fact]
    public void Skills_all_must_haves_matched_returns_one()
    {
        var resume = BuildResume("C#", "dotnet");
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave), ("dotnet", RequirementPriority.MustHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(1.0);
    }

    [Fact]
    public void Skills_half_must_haves_matched_returns_half()
    {
        var resume = BuildResume("C#");
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave), ("dotnet", RequirementPriority.MustHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(0.5);
    }

    [Fact]
    public void Skills_no_match_returns_zero()
    {
        var resume = BuildResume("java");
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave), ("dotnet", RequirementPriority.MustHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(0.0);
    }

    [Fact]
    public void Skills_nice_to_have_counts_half_weight()
    {
        var resume = BuildResume("python");
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave), ("python", RequirementPriority.NiceToHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().BeApproximately(0.5 / 1.5, 0.0001);
    }

    [Fact]
    public void Skills_match_is_case_insensitive()
    {
        var resume = BuildResume("csharp");
        var jobPosting = BuildJobPosting(("CSHARP", RequirementPriority.MustHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(1.0);
    }

    [Fact]
    public void Skills_requirement_matches_skill_keywords()
    {
        var resume = BuildResume();
        resume.AddSkill(Skill.Create(Technology.Create("backend")) with { Keywords = ["dotnet"] });
        var jobPosting = BuildJobPosting(("dotnet", RequirementPriority.MustHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(1.0);
    }

    [Fact]
    public void Experience_five_years_or_more_returns_one()
    {
        var resume = BuildResume();
        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Acme"),
            "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-6))));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.ExperienceScore.Should().Be(1.0);
    }

    [Fact]
    public void Experience_half_of_five_years_returns_half()
    {
        var resume = BuildResume();
        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Acme"),
            "Backend Developer",
            DateRange.Create(ReferenceDate.AddDays(-912), ReferenceDate)));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.ExperienceScore.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Experience_volunteer_work_is_excluded()
    {
        var resume = BuildResume();
        resume.AddExperience(new Experience(
            ExperienceType.Volunteer,
            OrganizationName.Create("Charity"),
            "Mentor",
            DateRange.Create(ReferenceDate.AddYears(-10))));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.ExperienceScore.Should().Be(0.0);
    }

    [Fact]
    public void Experience_none_returns_zero()
    {
        var result = _engine.Score(BuildResume(), BuildJobPosting(), ReferenceDate);

        result.ExperienceScore.Should().Be(0.0);
    }

    [Fact]
    public void Education_none_returns_zero()
    {
        var result = _engine.Score(BuildResume(), BuildJobPosting(), ReferenceDate);

        result.EducationScore.Should().Be(0.0);
    }

    [Fact]
    public void Certifications_none_returns_zero()
    {
        var result = _engine.Score(BuildResume(), BuildJobPosting(), ReferenceDate);

        result.CertificationsScore.Should().Be(0.0);
    }

    [Fact]
    public void Projects_none_returns_zero()
    {
        var result = _engine.Score(BuildResume(), BuildJobPosting(), ReferenceDate);

        result.ProjectsScore.Should().Be(0.0);
    }

    [Fact]
    public void Weighted_total_combines_all_sub_scores()
    {
        var resume = BuildResume("C#");
        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Acme"),
            "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-6))));
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.WeightedTotal.Should().BeApproximately(0.45 + 0.20, 0.0001);
    }
}

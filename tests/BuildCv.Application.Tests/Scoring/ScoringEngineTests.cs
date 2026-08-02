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
    public void Education_with_degree_returns_one()
    {
        var resume = BuildResume();
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), "BSc", "Computer Science",
            DateRange.Create(ReferenceDate.AddYears(-6), ReferenceDate.AddYears(-2)), null));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.EducationScore.Should().Be(1.0);
    }

    [Fact]
    public void Education_without_degree_returns_point_seven()
    {
        var resume = BuildResume();
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-6), ReferenceDate.AddYears(-2)), null));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.EducationScore.Should().Be(0.7);
    }

    [Fact]
    public void Certifications_valid_without_validity_period_counts()
    {
        var resume = BuildResume();
        resume.AddCertificate(new Certificate(
            "AWS Solutions Architect", OrganizationName.Create("Amazon"), null, null, null));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.CertificationsScore.Should().BeApproximately(1.0 / 3.0, 0.0001);
    }

    [Fact]
    public void Certifications_three_valid_returns_one()
    {
        var resume = BuildResume();
        resume.AddCertificate(new Certificate("Cert A", OrganizationName.Create("Amazon"), null, null, null));
        resume.AddCertificate(new Certificate("Cert B", OrganizationName.Create("Microsoft"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-1))));
        resume.AddCertificate(new Certificate("Cert C", OrganizationName.Create("Google"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-2), ReferenceDate.AddDays(30))));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.CertificationsScore.Should().Be(1.0);
    }

    [Fact]
    public void Certifications_expired_is_excluded()
    {
        var resume = BuildResume();
        resume.AddCertificate(new Certificate(
            "Expired Cert", OrganizationName.Create("Amazon"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-2), ReferenceDate.AddDays(-1))));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.CertificationsScore.Should().Be(0.0);
    }

    [Fact]
    public void Certifications_current_counts()
    {
        var resume = BuildResume();
        resume.AddCertificate(new Certificate(
            "Current Cert", OrganizationName.Create("Amazon"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-1))));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.CertificationsScore.Should().BeApproximately(1.0 / 3.0, 0.0001);
    }

    [Fact]
    public void Projects_with_technologies_counts()
    {
        var resume = BuildResume();
        resume.AddProject(new Project("Side project", DateRange.Create(ReferenceDate.AddYears(-1)))
        {
            Technologies = [Technology.Create("dotnet")],
        });

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.ProjectsScore.Should().BeApproximately(1.0 / 3.0, 0.0001);
    }

    [Fact]
    public void Skills_requirement_matches_project_technologies()
    {
        var resume = BuildResume();
        resume.AddProject(new Project("Side project", DateRange.Create(ReferenceDate.AddYears(-1)))
        {
            Technologies = [Technology.Create("dotnet")],
        });
        var jobPosting = BuildJobPosting(("dotnet", RequirementPriority.MustHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(1.0);
    }

    [Fact]
    public void Projects_three_qualifying_returns_one()
    {
        var resume = BuildResume();
        resume.AddProject(new Project("Project A", DateRange.Create(ReferenceDate.AddYears(-3)))
        {
            Technologies = [Technology.Create("dotnet")],
        });
        resume.AddProject(new Project("Project B", DateRange.Create(ReferenceDate.AddYears(-2)))
        {
            Technologies = [Technology.Create("postgres")],
        });
        resume.AddProject(new Project("Project C", DateRange.Create(ReferenceDate.AddYears(-1)))
        {
            Highlights = ["10k monthly active users"],
        });

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.ProjectsScore.Should().Be(1.0);
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

    // THE assertion for this PR: adding a sixth section changed nobody's score.
    //
    // The legacy weights are written out as literals rather than read back off the snapshot — a test
    // that asked the weights what the weights are would only ever agree with itself. These five
    // numbers are what shipped before Languages existed, and the total the engine produces today has
    // to equal the total that formula produces, exactly.
    //
    // The resume scores non-zero in all five sections, EDUCATION INCLUDED. That is what makes the
    // test bite: the regression this guards against moved Education from 0.20 to 0.10 against a
    // hard-coded 0.0 Languages score, so a resume with no education would have sailed through it
    // while every educated candidate silently lost up to ten points and a band with them.
    [Fact]
    public void Weighted_total_is_identical_to_the_five_section_model_it_replaced()
    {
        var resume = BuildResume("C#", "SQL");
        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Acme"),
            "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-3))));
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), "BSc", "Computer Science",
            DateRange.Create(ReferenceDate.AddYears(-8), ReferenceDate.AddYears(-4)), null));
        resume.AddCertificate(new Certificate(
            "Azure Architect", OrganizationName.Create("Microsoft"), null, null, null));
        resume.AddProject(new Project("Side project", DateRange.Create(ReferenceDate.AddYears(-1)))
        {
            Technologies = [Technology.Create("dotnet")],
        });
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave), ("SQL", RequirementPriority.NiceToHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        var legacy =
            0.45 * result.SkillsScore +
            0.20 * result.ExperienceScore +
            0.20 * result.EducationScore +
            0.10 * result.CertificationsScore +
            0.05 * result.ProjectsScore;

        // Exact, not approximate. Adding `+ 0.0 * languagesScore` to a sum leaves every bit of it
        // alone, so anything less than exact equality would tolerate a weight that really did move.
        result.WeightedTotal.Should().Be(legacy);

        // And the five sections it is built from really are all in play, or the equality above would
        // hold for an empty resume too.
        result.EducationScore.Should().BeGreaterThan(0.0, "the regression is invisible without education");
        result.SkillsScore.Should().BeGreaterThan(0.0);
        result.ExperienceScore.Should().BeGreaterThan(0.0);
        result.CertificationsScore.Should().BeGreaterThan(0.0);
        result.ProjectsScore.Should().BeGreaterThan(0.0);
    }

    // The ceiling, stated on its own. A perfect resume has to be able to reach 1.00 — weighting an
    // uncomputed section caps it below that, which is a bug no per-section assertion would name.
    [Fact]
    public void Weighted_total_can_still_reach_one_for_a_resume_that_scores_perfectly()
    {
        var resume = BuildResume("C#");
        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Acme"),
            "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-6))));
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), "BSc", "Computer Science",
            DateRange.Create(ReferenceDate.AddYears(-10), ReferenceDate.AddYears(-6)), null));
        foreach (var issuer in new[] { "Amazon", "Microsoft", "Google" })
            resume.AddCertificate(new Certificate($"Cert {issuer}", OrganizationName.Create(issuer), null, null, null));
        foreach (var name in new[] { "Project A", "Project B", "Project C" })
            resume.AddProject(new Project(name, DateRange.Create(ReferenceDate.AddYears(-1)))
            {
                Technologies = [Technology.Create("dotnet")],
            });
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.WeightedTotal.Should().BeApproximately(1.0, 0.0001,
            "a section carrying weight that nothing computes lowers the maximum achievable score");
    }
}

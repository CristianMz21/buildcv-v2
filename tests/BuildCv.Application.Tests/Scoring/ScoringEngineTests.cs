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

    // The builder above states no weight, so every posting it makes carries the weight the priority
    // would have derived anyway. That is what made the whole suite blind to WHICH of the two the engine
    // read — PR 2's negative control swapped Priority for Weight and only the two tests written for it
    // noticed. This builder is how a test states a magnitude that CONTRADICTS the priority, which is
    // the only shape of input one number can tell the two models apart on.
    private static JobPosting BuildWeightedJobPosting(
        params (string Skill, RequirementPriority Priority, double Weight)[] requirements)
    {
        var jobPosting = JobPosting.Create(AccountId.New(), "Backend Developer", OrganizationName.Create("Acme"));
        foreach (var (skill, priority, weight) in requirements)
            jobPosting.AddRequirement(JobRequirement.Create(Technology.Create(skill), priority, weight));
        return jobPosting;
    }

    private static JobPosting WithLanguages(
        JobPosting jobPosting, params (string Name, LanguageProficiency Minimum)[] languages)
    {
        foreach (var (name, minimum) in languages)
            jobPosting.AddLanguageRequirement(LanguageRequirement.Create(name, minimum));
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

        // 0.45*1.0 (skills) + 0.20*1.0 (experience) + 0.10*0.5 (languages: the posting asks for none,
        // so the section returns the neutral 0.5) = 0.70. Everything else scores zero.
        result.WeightedTotal.Should().BeApproximately(0.45 + 0.20 + (0.10 * 0.5), 0.0001);
    }

    // THE assertion for this release, and it is the INVERSE of the one it replaces.
    //
    // Until now this file pinned that the six-section total was bit-for-bit the five-section total,
    // because Languages carried no weight. This release moves 0.10 from Education to Languages and
    // starts computing the section, so the two models must now DISAGREE — and by a stated amount, not
    // merely somewhere.
    //
    // Both weight sets are written out as literals rather than read off the snapshot: a test that asks
    // the weights what the weights are only ever agrees with itself.
    //
    // The resume scores non-zero in all six sections, EDUCATION INCLUDED, which is what makes the
    // difference measurable — the whole of the change is Education losing half its weight and
    // Languages gaining it, and a resume with no education would show only half of that.
    [Fact]
    public void Weighted_total_now_differs_from_the_five_section_model_it_replaced()
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

        var shipped =
            0.45 * result.SkillsScore +
            0.20 * result.ExperienceScore +
            0.10 * result.EducationScore +
            0.10 * result.CertificationsScore +
            0.05 * result.ProjectsScore +
            0.10 * result.LanguagesScore;

        result.WeightedTotal.Should().BeApproximately(shipped, 1e-12);

        // The size of the move, stated rather than left as "different". This resume holds a degree
        // (Education 1.0) and the posting states no language requirement (Languages 0.5), so the
        // candidate loses 0.10 of Education and regains 0.05 of Languages: exactly -0.05.
        (result.WeightedTotal - legacy).Should().BeApproximately(-0.05, 1e-9,
            "Education halved and Languages was funded with what it lost");

        // And all six sections it is built from really are in play, or the difference above could come
        // from a section that happens to score zero either way.
        result.EducationScore.Should().BeGreaterThan(0.0, "half the move is invisible without education");
        result.LanguagesScore.Should().BeGreaterThan(0.0, "the other half is invisible without a Languages score");
        result.SkillsScore.Should().BeGreaterThan(0.0);
        result.ExperienceScore.Should().BeGreaterThan(0.0);
        result.CertificationsScore.Should().BeGreaterThan(0.0);
        result.ProjectsScore.Should().BeGreaterThan(0.0);
    }

    // The ceiling, stated on its own. A perfect resume has to be able to reach 1.00 - a section
    // carrying weight that the resume cannot fully satisfy caps it below that, which is a bug no
    // per-section assertion would name.
    //
    // NOTE WHAT PERFECT NOW REQUIRES: the posting has to state a language requirement and the resume
    // has to meet it. Against a posting that asks for no language the Languages section returns the
    // neutral 0.5 and NO resume can exceed 0.95 - see the test immediately below, which pins that as
    // deliberate rather than letting it be discovered.
    [Fact]
    public void Weighted_total_can_still_reach_one_for_a_resume_that_scores_perfectly()
    {
        var resume = BuildPerfectResume();
        resume.AddLanguage(new Language("English", "Native speaker", LanguageProficiency.Native));
        var jobPosting = WithLanguages(
            BuildJobPosting(("C#", RequirementPriority.MustHave)),
            ("English", LanguageProficiency.Professional));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.WeightedTotal.Should().BeApproximately(1.0, 0.0001,
            "a section carrying weight that nothing can satisfy lowers the maximum achievable score");
    }

    // The price of the neutral 0.5, named out loud because it is the most surprising consequence of
    // weighting Languages: a posting that states no language requirement caps every candidate at 0.95.
    //
    // Neutral means neither rewarding nor punishing RELATIVE TO THE MIDPOINT of the section, not
    // relative to its ceiling. The skills section has had the same property since long before this
    // release - it is only newly visible because Languages is the section most postings say nothing
    // about.
    [Fact]
    public void Weighted_total_is_capped_below_one_when_the_posting_states_no_language_requirement()
    {
        var resume = BuildPerfectResume();
        resume.AddLanguage(new Language("English", "Native speaker", LanguageProficiency.Native));

        var result = _engine.Score(resume, BuildJobPosting(("C#", RequirementPriority.MustHave)), ReferenceDate);

        result.LanguagesScore.Should().Be(0.5, "no requirement means no opinion, in either direction");
        result.WeightedTotal.Should().BeApproximately(0.95, 0.0001,
            "half of the 0.10 Languages weight is unreachable when the posting asks for no language");
    }

    // The engine now reads JobRequirement.Weight, and uses Priority only as the must-have gate on a
    // recommendation. This is the INVERTED twin of the test that pinned the opposite.
    //
    // The weights deliberately CONTRADICT the priority-derived ones: read Weight and the score is
    // 10 / (10 + 0) = 1.0; read Priority and it is 1.0 / (1.0 + 0.5) = 0.667. One number tells the two
    // models apart, which is the only reason this test is worth having - and without it the suite goes
    // blind again, because every posting BuildJobPosting makes carries the priority-derived weight.
    [Fact]
    public void Skills_score_is_derived_from_the_requirement_weight_and_not_from_its_priority()
    {
        var resume = BuildResume("C#");
        var jobPosting = BuildWeightedJobPosting(
            ("C#", RequirementPriority.MustHave, 10.0),
            ("Go", RequirementPriority.NiceToHave, 0.0));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(1.0, "the matched requirement carries all ten of the stated weight");
        result.SkillsScore.Should().NotBe(1.0 / 1.5, "that is the answer a priority-derived magnitude gives");
    }

    // A posting can state a weight the priority would never have derived, and it moves the total. The
    // inverted twin of the equality that pinned "derived and stated weights score identically".
    [Fact]
    public void Weighted_total_now_moves_with_the_stated_weight()
    {
        var resume = BuildResume("C#");

        var derived = BuildJobPosting(
            ("C#", RequirementPriority.MustHave), ("Go", RequirementPriority.NiceToHave));
        var stated = BuildWeightedJobPosting(
            ("C#", RequirementPriority.MustHave, 1.0), ("Go", RequirementPriority.NiceToHave, 1.0));

        derived.Requirements[1].Weight.Should().NotBe(stated.Requirements[1].Weight,
            "the two postings must really disagree about Weight, or this proves nothing");

        var derivedScore = _engine.Score(resume, derived, ReferenceDate);
        var statedScore = _engine.Score(resume, stated, ReferenceDate);

        derivedScore.SkillsScore.Should().BeApproximately(1.0 / 1.5, 0.0001, "the weights are 1.0 and 0.5");
        statedScore.SkillsScore.Should().Be(0.5, "the weights are 1.0 and 1.0");
        derivedScore.WeightedTotal.Should().NotBe(statedScore.WeightedTotal);
    }

    // Every requirement weighted 0.0 is a posting that expresses no opinion about skills, exactly like
    // a posting with no requirements at all - and without the guard it is a division by zero producing
    // NaN, which ScoreBreakdown.Create does NOT reject (both `NaN < 0` and `NaN > 1` are false).
    [Fact]
    public void Skills_all_zero_weights_falls_back_to_the_neutral_score()
    {
        var resume = BuildResume("C#");
        var jobPosting = BuildWeightedJobPosting(
            ("C#", RequirementPriority.MustHave, 0.0), ("Go", RequirementPriority.NiceToHave, 0.0));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(0.5);
        double.IsNaN(result.WeightedTotal).Should().BeFalse("0/0 would poison the whole total");
    }

    [Fact]
    public void Languages_no_requirements_returns_neutral()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", null, LanguageProficiency.Native));

        var result = _engine.Score(resume, BuildJobPosting(), ReferenceDate);

        result.LanguagesScore.Should().Be(0.5);
    }

    [Fact]
    public void Languages_held_above_the_required_level_is_satisfied()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", "Fluent", LanguageProficiency.Fluent));
        var jobPosting = WithLanguages(BuildJobPosting(), ("English", LanguageProficiency.Professional));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.LanguagesScore.Should().Be(1.0);
    }

    [Fact]
    public void Languages_held_exactly_at_the_required_level_is_satisfied()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", null, LanguageProficiency.Professional));
        var jobPosting = WithLanguages(BuildJobPosting(), ("English", LanguageProficiency.Professional));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.LanguagesScore.Should().Be(1.0, "the comparison is held >= required, not held > required");
    }

    [Fact]
    public void Languages_held_below_the_required_level_is_not_satisfied()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", "Some school English", LanguageProficiency.Basic));
        var jobPosting = WithLanguages(BuildJobPosting(), ("English", LanguageProficiency.Professional));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.LanguagesScore.Should().Be(0.0);
    }

    // A candidate holding the language with no Level recorded does NOT satisfy the requirement, and the
    // Fluency text beside it is never consulted - parsing free text into a level would score a native
    // speaker who wrote "Bilingue" at zero, which is the failure Language.Level exists to avoid. The
    // missing data becomes a recommendation instead of a silent penalty.
    [Fact]
    public void Languages_held_without_a_recorded_level_is_not_satisfied()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", "Native speaker", Level: null));
        var jobPosting = WithLanguages(BuildJobPosting(), ("English", LanguageProficiency.Professional));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.LanguagesScore.Should().Be(0.0, "Fluency is display text and is never read as a level");
    }

    [Fact]
    public void Languages_match_is_case_insensitive()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("english", null, LanguageProficiency.Native));
        var jobPosting = WithLanguages(BuildJobPosting(), ("ENGLISH", LanguageProficiency.Professional));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.LanguagesScore.Should().Be(1.0);
    }

    [Fact]
    public void Languages_score_is_the_satisfied_share_of_the_stated_requirements()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", null, LanguageProficiency.Native));
        resume.AddLanguage(new Language("German", null, LanguageProficiency.Basic));
        var jobPosting = WithLanguages(
            BuildJobPosting(),
            ("English", LanguageProficiency.Professional),
            ("German", LanguageProficiency.Professional),
            ("French", LanguageProficiency.Basic));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.LanguagesScore.Should().BeApproximately(1.0 / 3.0, 0.0001,
            "English satisfies, German is below level, French is missing");
    }

    private static Resume BuildPerfectResume()
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
        return resume;
    }
}

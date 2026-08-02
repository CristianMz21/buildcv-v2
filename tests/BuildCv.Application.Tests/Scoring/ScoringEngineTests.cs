using BuildCv.Application.Scoring;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Application.Tests.Scoring;

public class ScoringEngineTests
{
    private static readonly DateOnly ReferenceDate = new(2025, 1, 1);

    private readonly ScoringEngine _engine = new();

    // Score now returns a ScoreResult: the six numbers AND the advice derived from the same pass. Every
    // test in this file is about the numbers, so they read through the breakdown and the advice has its
    // own files — RecommendationBuilderTests for what is emitted, ActingOnARecommendationTests for the
    // claim that each Impact is the exact score a candidate would gain.
    private ScoreBreakdown ScoreBreakdownOf(Resume resume, JobPosting jobPosting, DateOnly referenceDate) =>
        _engine.Score(resume, jobPosting, referenceDate).Breakdown;

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

    // A posting that states no skill requirement does not ASK about skills, so the section scores
    // nothing and carries no weight — it is renormalized out of the total rather than handed a neutral
    // 0.5 that quietly cost every candidate half of 0.45.
    [Fact]
    public void Skills_no_requirements_does_not_apply()
    {
        var result = ScoreBreakdownOf(BuildResume("C#"), BuildJobPosting(), ReferenceDate);

        result.SkillsScore.Should().Be(0.0, "nothing was measured");
        result.Weights.Skills.Should().Be(0.0, "and nothing was measured against");
    }

    [Fact]
    public void Skills_all_must_haves_matched_returns_one()
    {
        var resume = BuildResume("C#", "dotnet");
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave), ("dotnet", RequirementPriority.MustHave));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(1.0);
    }

    [Fact]
    public void Skills_half_must_haves_matched_returns_half()
    {
        var resume = BuildResume("C#");
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave), ("dotnet", RequirementPriority.MustHave));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(0.5);
    }

    [Fact]
    public void Skills_no_match_returns_zero()
    {
        var resume = BuildResume("java");
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave), ("dotnet", RequirementPriority.MustHave));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(0.0);
    }

    [Fact]
    public void Skills_nice_to_have_counts_half_weight()
    {
        var resume = BuildResume("python");
        var jobPosting = BuildJobPosting(("C#", RequirementPriority.MustHave), ("python", RequirementPriority.NiceToHave));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().BeApproximately(0.5 / 1.5, 0.0001);
    }

    [Fact]
    public void Skills_match_is_case_insensitive()
    {
        var resume = BuildResume("csharp");
        var jobPosting = BuildJobPosting(("CSHARP", RequirementPriority.MustHave));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(1.0);
    }

    [Fact]
    public void Skills_requirement_matches_skill_keywords()
    {
        var resume = BuildResume();
        resume.AddSkill(Skill.Create(Technology.Create("backend")) with { Keywords = ["dotnet"] });
        var jobPosting = BuildJobPosting(("dotnet", RequirementPriority.MustHave));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

        result.ExperienceScore.Should().Be(0.0);
    }

    [Fact]
    public void Experience_none_returns_zero()
    {
        var result = ScoreBreakdownOf(BuildResume(), BuildJobPosting(), ReferenceDate);

        result.ExperienceScore.Should().Be(0.0);
    }

    [Fact]
    public void Education_none_returns_zero()
    {
        var result = ScoreBreakdownOf(BuildResume(), BuildJobPosting(), ReferenceDate);

        result.EducationScore.Should().Be(0.0);
    }

    [Fact]
    public void Certifications_none_returns_zero()
    {
        var result = ScoreBreakdownOf(BuildResume(), BuildJobPosting(), ReferenceDate);

        result.CertificationsScore.Should().Be(0.0);
    }

    [Fact]
    public void Projects_none_returns_zero()
    {
        var result = ScoreBreakdownOf(BuildResume(), BuildJobPosting(), ReferenceDate);

        result.ProjectsScore.Should().Be(0.0);
    }

    [Fact]
    public void Education_with_degree_returns_one()
    {
        var resume = BuildResume();
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), "BSc", "Computer Science",
            DateRange.Create(ReferenceDate.AddYears(-6), ReferenceDate.AddYears(-2)), null));

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

        result.EducationScore.Should().Be(1.0);
    }

    [Fact]
    public void Education_without_degree_returns_point_seven()
    {
        var resume = BuildResume();
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-6), ReferenceDate.AddYears(-2)), null));

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

        result.EducationScore.Should().Be(0.7);
    }

    [Fact]
    public void Certifications_valid_without_validity_period_counts()
    {
        var resume = BuildResume();
        resume.AddCertificate(new Certificate(
            "AWS Solutions Architect", OrganizationName.Create("Amazon"), null, null, null));

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

        result.CertificationsScore.Should().Be(1.0);
    }

    [Fact]
    public void Certifications_expired_is_excluded()
    {
        var resume = BuildResume();
        resume.AddCertificate(new Certificate(
            "Expired Cert", OrganizationName.Create("Amazon"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-2), ReferenceDate.AddDays(-1))));

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

        result.CertificationsScore.Should().Be(0.0);
    }

    [Fact]
    public void Certifications_current_counts()
    {
        var resume = BuildResume();
        resume.AddCertificate(new Certificate(
            "Current Cert", OrganizationName.Create("Amazon"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-1))));

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        // The posting states no language requirement, so that section is renormalized out and the other
        // five are scored out of 0.90: Skills 0.45/0.90 = 0.50, Experience 0.20/0.90 = 0.2222.
        // Total = 0.50*1.0 + 0.2222*1.0 = 0.65/0.90 = 0.7222. Everything else on this resume scores zero.
        result.WeightedTotal.Should().BeApproximately(0.65 / 0.90, 0.0001);
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
    //
    // The posting states BOTH a skill and a language requirement, so every section applies and
    // renormalization is the identity: the weights here are Default() bit-for-bit, asserted below
    // rather than assumed. That is what lets the six literals stand as the shipped weighting — against
    // a posting that asked less, the renormalized set would differ and the comparison would be to a
    // model neither of these two is.
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
        resume.AddLanguage(new Language("English", null, LanguageProficiency.Native));
        var jobPosting = WithLanguages(
            BuildJobPosting(("C#", RequirementPriority.MustHave), ("SQL", RequirementPriority.NiceToHave)),
            ("English", LanguageProficiency.Professional),
            ("German", LanguageProficiency.Professional));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.Weights.Should().Be(ScoringWeightsSnapshot.Default(),
            "every section applies here, so renormalization divides by 1.0 and changes nothing");

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
        // (Education 1.0) and satisfies one of the posting's two stated languages (Languages 0.5), so
        // the candidate loses 0.10 of Education and regains 0.10 * 0.5 of Languages: exactly -0.05.
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

    // The ceiling, on a posting that asks about everything. A perfect resume has to be able to reach
    // 1.00 - a section carrying weight the resume cannot satisfy caps it below that, which is a bug no
    // per-section assertion would name.
    //
    // This is the case where renormalization is the IDENTITY: every section applies, so the divisor is
    // 1.00 and the weights are Default() bit-for-bit. The two tests below take the same ceiling through
    // the cases where it is not - a posting that asks about less, which is where the old design failed.
    [Fact]
    public void Weighted_total_can_still_reach_one_for_a_resume_that_scores_perfectly()
    {
        var resume = BuildPerfectResume();
        var jobPosting = WithLanguages(
            BuildJobPosting(("C#", RequirementPriority.MustHave)),
            ("English", LanguageProficiency.Professional));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.WeightedTotal.Should().BeApproximately(1.0, 0.0001,
            "a section carrying weight that nothing can satisfy lowers the maximum achievable score");
    }

    // THE ASSERTION THE PREVIOUS DESIGN COULD NOT SATISFY, and the reason renormalization exists.
    //
    // A posting stating no language requirement is the COMMON case. Under the neutral 0.5 it capped
    // every candidate at 0.95: a flawless CV scored 95 and the candidate had no way to find out why, in
    // a product whose entire purpose is explaining their score to them. The unasked section now carries
    // no weight at all, so the ceiling is 1.00 for every posting.
    [Fact]
    public void Weighted_total_reaches_one_even_when_the_posting_states_no_language_requirement()
    {
        var resume = BuildPerfectResume();

        var result = ScoreBreakdownOf(resume, BuildJobPosting(("C#", RequirementPriority.MustHave)), ReferenceDate);

        result.Weights.Languages.Should().Be(0.0, "the posting asked nothing of it");
        result.Weights.Skills.Should().BeApproximately(0.45 / 0.90, 0.0001,
            "the other five share the tenth Languages gave up, in proportion");
        result.WeightedTotal.Should().BeApproximately(1.0, 0.0001,
            "an unasked section must not make a perfect score unreachable");
    }

    // The same property one step further: a posting that asks for NOTHING at all is scored out of the
    // four sections that read the candidate's own data, and a resume perfect in those four still
    // reaches 1.00. Under the old design this candidate was capped at 0.775 by two neutral halves.
    [Fact]
    public void Weighted_total_reaches_one_when_the_posting_asks_for_nothing_at_all()
    {
        var result = ScoreBreakdownOf(BuildPerfectResume(), BuildJobPosting(), ReferenceDate);

        result.Weights.Skills.Should().Be(0.0);
        result.Weights.Languages.Should().Be(0.0);
        result.Weights.Experience.Should().BeApproximately(0.20 / 0.45, 0.0001);
        result.WeightedTotal.Should().BeApproximately(1.0, 0.0001);
    }

    // Stated as a PROPERTY over every shape of posting rather than as the four cases above, because the
    // invariant is what the whole design rests on: the persisted weights are what explain the score, so
    // a set that did not sum to 1.0 would be a row whose numbers cannot add up.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Weights_alwaysSumToOne_whateverThePostingAsksFor(bool statesSkills, bool statesLanguages)
    {
        var jobPosting = statesSkills
            ? BuildJobPosting(("C#", RequirementPriority.MustHave))
            : BuildJobPosting();
        if (statesLanguages)
            WithLanguages(jobPosting, ("English", LanguageProficiency.Professional));

        var result = ScoreBreakdownOf(BuildPerfectResume(), jobPosting, ReferenceDate);

        Enum.GetValues<SectionType>().Sum(result.Weights.WeightFor)
            .Should().BeApproximately(1.0, 0.0001);

        // And the ceiling really is reachable in every one of those shapes. BuildPerfectResume satisfies
        // every section the posting can ask about, including the language requirement when there is one.
        result.WeightedTotal.Should().BeApproximately(1.0, 0.0001,
            "a candidate who satisfies every applicable section scores exactly 1.0");
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

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

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

        var derivedScore = ScoreBreakdownOf(resume, derived, ReferenceDate);
        var statedScore = ScoreBreakdownOf(resume, stated, ReferenceDate);

        derivedScore.SkillsScore.Should().BeApproximately(1.0 / 1.5, 0.0001, "the weights are 1.0 and 0.5");
        statedScore.SkillsScore.Should().Be(0.5, "the weights are 1.0 and 1.0");
        derivedScore.WeightedTotal.Should().NotBe(statedScore.WeightedTotal);
    }

    // Every requirement weighted 0.0 expresses no SCOREABLE opinion about skills, exactly like a posting
    // with no requirements at all, and the same guard covers both — it is also what stops the share
    // being 0/0. NaN is worth naming because it would sail past ScoreBreakdown's range check (`NaN < 0`
    // and `NaN > 1` are both false) and poison the whole total; the finiteness guard added alongside
    // renormalization is the backstop, and this is the input that would reach it.
    [Fact]
    public void Skills_all_zero_weights_does_not_apply()
    {
        var resume = BuildResume("C#");
        var jobPosting = BuildWeightedJobPosting(
            ("C#", RequirementPriority.MustHave, 0.0), ("Go", RequirementPriority.NiceToHave, 0.0));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.SkillsScore.Should().Be(0.0);
        result.Weights.Skills.Should().Be(0.0);
        double.IsNaN(result.WeightedTotal).Should().BeFalse("0/0 would poison the whole total");
    }

    [Fact]
    public void Languages_no_requirements_does_not_apply()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", null, LanguageProficiency.Native));

        var result = ScoreBreakdownOf(resume, BuildJobPosting(), ReferenceDate);

        result.LanguagesScore.Should().Be(0.0, "nothing was measured");
        result.Weights.Languages.Should().Be(0.0, "and nothing was measured against");
    }

    [Fact]
    public void Languages_held_above_the_required_level_is_satisfied()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", "Fluent", LanguageProficiency.Fluent));
        var jobPosting = WithLanguages(BuildJobPosting(), ("English", LanguageProficiency.Professional));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.LanguagesScore.Should().Be(1.0);
    }

    [Fact]
    public void Languages_held_exactly_at_the_required_level_is_satisfied()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", null, LanguageProficiency.Professional));
        var jobPosting = WithLanguages(BuildJobPosting(), ("English", LanguageProficiency.Professional));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.LanguagesScore.Should().Be(1.0, "the comparison is held >= required, not held > required");
    }

    [Fact]
    public void Languages_held_below_the_required_level_is_not_satisfied()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", "Some school English", LanguageProficiency.Basic));
        var jobPosting = WithLanguages(BuildJobPosting(), ("English", LanguageProficiency.Professional));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

        result.LanguagesScore.Should().Be(0.0, "Fluency is display text and is never read as a level");
    }

    [Fact]
    public void Languages_match_is_case_insensitive()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("english", null, LanguageProficiency.Native));
        var jobPosting = WithLanguages(BuildJobPosting(), ("ENGLISH", LanguageProficiency.Professional));

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

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

        var result = ScoreBreakdownOf(resume, jobPosting, ReferenceDate);

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
        // Perfect in every section a posting can ask about, LANGUAGES INCLUDED. Without this the resume
        // is only perfect against postings that state no language requirement, and the ceiling tests
        // would be asserting the very gap they exist to rule out.
        resume.AddLanguage(new Language("English", "Native speaker", LanguageProficiency.Native));
        return resume;
    }
}

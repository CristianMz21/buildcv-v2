using BuildCv.Application.Scoring;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Application.Tests.Scoring;

// What the rule set emits, and what it refuses to. The claim that each Impact is exact lives next
// door in ActingOnARecommendationTests, which measures it by re-scoring.
public class RecommendationBuilderTests
{
    private static readonly DateOnly ReferenceDate = new(2025, 1, 1);

    private readonly ScoringEngine _engine = new();

    private IReadOnlyList<Recommendation> AdviceFor(Resume resume, JobPosting jobPosting) =>
        _engine.Score(resume, jobPosting, ReferenceDate).Recommendations;

    // The test that catches "we always emit five bullets". A resume with nothing left to fix has to get
    // nothing back — advice that appears regardless of the input is decoration, not advice.
    //
    // "Every requirement" includes the ones the RESUME is measured against, not only the ones the
    // posting states: the certifications, projects and education rules fire on a resume being below its
    // own caps and have no posting side at all.
    [Fact]
    public void NoRecommendations_WhenEveryRequirementIsMet()
    {
        var resume = BuildPerfectResume();
        var jobPosting = WithLanguages(
            BuildJobPosting(("C#", RequirementPriority.MustHave)),
            ("English", LanguageProficiency.Professional));

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.WeightedTotal.Should().BeApproximately(1.0, 0.0001, "there is nothing left to advise about");
        result.Recommendations.Should().BeEmpty();
    }

    // Σ Impact per section can never promise more than the section has left to give. Phrased as an
    // inequality rather than an equality because the cap of ten can drop advice off the end, and
    // because the education rules aim at the ceiling from wherever they start.
    [Fact]
    public void Impact_NeverExceedsTheHeadroomOfItsSection()
    {
        var resume = BuildThoroughlyImperfectResume();
        var jobPosting = BuildThoroughlyDemandingPosting();

        var result = _engine.Score(resume, jobPosting, ReferenceDate);

        result.Recommendations.Should().NotBeEmpty("an empty set satisfies every inequality below");

        foreach (var section in Enum.GetValues<SectionType>())
        {
            var promised = result.Recommendations.Where(r => r.Section == section).Sum(r => r.Impact);
            var headroom = result.Weights.WeightFor(section) * (1.0 - result.Breakdown.ScoreFor(section));

            promised.Should().BeLessThanOrEqualTo(headroom + 1e-9,
                $"{section} cannot pay out more than it has left");
        }
    }

    // Two missing requirements are two pieces of advice, not one lumped "add the missing skills". The
    // impacts differ per requirement, and a candidate deciding where to spend an afternoon needs them
    // apart.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void RecommendationCount_TracksTheNumberOfUnmetRequirements(int unmet, int expected)
    {
        var held = new[] { "C#", "SQL", "Docker" };
        var resume = BuildResume([.. held.Take(held.Length - unmet)]);
        var jobPosting = BuildJobPosting([.. held.Select(s => (s, RequirementPriority.MustHave))]);

        var skillsAdvice = AdviceFor(resume, jobPosting).Where(r => r.Section == SectionType.Skills).ToList();

        skillsAdvice.Should().HaveCount(expected);
        skillsAdvice.Select(r => r.Message).Should().OnlyHaveUniqueItems(
            "one sentence per requirement, naming the requirement");
    }

    [Fact]
    public void NoSkillsRecommendation_WhenEveryRequirementMatches()
    {
        var resume = BuildResume("C#", "SQL");
        var jobPosting = BuildJobPosting(
            ("C#", RequirementPriority.MustHave), ("SQL", RequirementPriority.NiceToHave));

        AdviceFor(resume, jobPosting).Should().NotContain(r => r.Section == SectionType.Skills);
    }

    [Fact]
    public void NoCertificationsRecommendation_WhenTheSectionIsAtItsCap()
    {
        var resume = BuildResume();
        foreach (var issuer in new[] { "Amazon", "Microsoft", "Google" })
            resume.AddCertificate(new Certificate($"Cert {issuer}", OrganizationName.Create(issuer), null, null, null));

        AdviceFor(resume, BuildJobPosting()).Should()
            .NotContain(r => r.Kind == RecommendationKind.FewerCertificationsThanExpected);
    }

    [Fact]
    public void NoProjectsRecommendation_WhenTheSectionIsAtItsCap()
    {
        var resume = BuildResume();
        foreach (var name in new[] { "A", "B", "C" })
            resume.AddProject(new Project(name, DateRange.Create(ReferenceDate.AddYears(-1)))
            {
                Highlights = ["shipped it"],
            });

        AdviceFor(resume, BuildJobPosting()).Should()
            .NotContain(r => r.Kind == RecommendationKind.FewerProjectsThanExpected);
    }

    [Fact]
    public void NoEducationRecommendation_WhenADegreeIsRecorded()
    {
        var resume = BuildResume();
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), "BSc", "Computer Science",
            DateRange.Create(ReferenceDate.AddYears(-8), ReferenceDate.AddYears(-4)), null));

        AdviceFor(resume, BuildJobPosting()).Should().NotContain(r => r.Section == SectionType.Education);
    }

    [Fact]
    public void NoLanguagesRecommendation_WhenEveryStatedRequirementIsSatisfied()
    {
        var resume = BuildResume();
        resume.AddLanguage(new Language("English", null, LanguageProficiency.Native));
        var jobPosting = WithLanguages(BuildJobPosting(), ("English", LanguageProficiency.Professional));

        AdviceFor(resume, jobPosting).Should().NotContain(r => r.Section == SectionType.Languages);
    }

    // A posting that asks for no language asks for no advice about languages either. The section still
    // scores 0.5, and there is nothing a candidate could do about a question nobody put.
    [Fact]
    public void NoLanguagesRecommendation_WhenThePostingStatesNoRequirement()
    {
        var resume = BuildResume();

        AdviceFor(resume, BuildJobPosting()).Should().NotContain(r => r.Section == SectionType.Languages);
    }

    // The one experience rule fires only on a mis-tagged entry, never on "you are short of five years".
    // A resume with nothing but professional work and two years on it gets no experience advice at all,
    // which is the omission the rule set is designed around.
    [Fact]
    public void NoExperienceRecommendation_WhenTheOnlyGapIsTime()
    {
        var resume = BuildResume();
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-2), ReferenceDate)));

        var advice = AdviceFor(resume, BuildJobPosting());

        advice.Should().NotContain(r => r.Section == SectionType.Experience,
            "'have more years of experience' is a fact about time, not advice");
        advice.Should().NotBeEmpty("the resume is otherwise empty, so the other sections must have spoken");
    }

    // Re-labelling volunteer work is not advice when the section is already at its cap, and it would be
    // advice to misrepresent unpaid work for a gain of zero.
    [Fact]
    public void NoExperienceRecommendation_WhenTheSectionIsAlreadyAtItsCap()
    {
        var resume = BuildResume();
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-8))));
        resume.AddExperience(new Experience(
            ExperienceType.Volunteer, OrganizationName.Create("Charity"), "Mentor",
            DateRange.Create(ReferenceDate.AddYears(-3))));

        AdviceFor(resume, BuildJobPosting()).Should()
            .NotContain(r => r.Kind == RecommendationKind.ExperienceNotMarkedProfessional);
    }

    // Priority is a pure function of Impact plus the must-have gate. These are the numbers the shipped
    // weights actually produce, written out so the two halves can never drift apart silently.
    [Fact]
    public void Priority_isDecidedByImpactAndTheMustHaveGate()
    {
        var resume = BuildThoroughlyImperfectResume();
        var jobPosting = BuildThoroughlyDemandingPosting();

        var advice = AdviceFor(resume, jobPosting);

        foreach (var recommendation in advice)
        {
            var expected = recommendation.Kind == RecommendationKind.MissingMustHaveSkill
                ? RecommendationPriority.Critical
                : recommendation.Impact >= 0.10 ? RecommendationPriority.Critical
                : recommendation.Impact >= 0.03 ? RecommendationPriority.Important
                : RecommendationPriority.NiceToHave;

            recommendation.Priority.Should().Be(expected,
                $"{recommendation.Kind} carries an impact of {recommendation.Impact}");
        }

        advice.Select(r => r.Priority).Distinct().Should().HaveCountGreaterThan(1,
            "a scenario where every rule lands on one priority would prove nothing about the function");
    }

    // The must-have gate, isolated. This requirement is worth 0.45 x (1/11) = 0.041, which the impact
    // thresholds alone would call Important — it is Critical because the posting screens on it.
    [Fact]
    public void AnUnmatchedMustHave_isCriticalEvenWhenItsImpactIsNot()
    {
        var resume = BuildResume("C#");
        var jobPosting = BuildJobPosting(
            ("C#", RequirementPriority.MustHave),
            ("SQL", RequirementPriority.MustHave));
        foreach (var extra in new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i" })
            jobPosting.AddRequirement(JobRequirement.Create(Technology.Create(extra), RequirementPriority.MustHave));
        foreach (var extra in new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i" })
            resume.AddSkill(Skill.Create(Technology.Create(extra)));

        var advice = AdviceFor(resume, jobPosting)
            .Should().ContainSingle(r => r.Kind == RecommendationKind.MissingMustHaveSkill).Subject;

        advice.Impact.Should().BeApproximately(0.45 * (1.0 / 11.0), 1e-9);
        advice.Impact.Should().BeLessThan(0.10, "or the gate would not be what made this Critical");
        advice.Priority.Should().Be(RecommendationPriority.Critical);
    }

    // A posting that weights every requirement at zero scores the neutral 0.5 whatever the resume holds,
    // so nothing a candidate does moves the section — and the advice is still emitted, with an impact of
    // exactly zero rather than a flattering guess.
    //
    // The must-have keeps its Critical label anyway. The gate is about what the posting SCREENS on, not
    // about what the score pays, and a recruiter reading "must have Go" does not care that this
    // deployment's weights made it worth nothing.
    [Fact]
    public void ZeroWeightedRequirements_stillProduceAdviceWithAnHonestZeroImpact()
    {
        var resume = BuildResume("C#");
        var jobPosting = JobPosting.Create(AccountId.New(), "Backend Developer", OrganizationName.Create("Acme"));
        jobPosting.AddRequirement(JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave, 0.0));
        jobPosting.AddRequirement(JobRequirement.Create(Technology.Create("Go"), RequirementPriority.MustHave, 0.0));
        jobPosting.AddRequirement(JobRequirement.Create(Technology.Create("Rust"), RequirementPriority.NiceToHave, 0.0));

        var advice = AdviceFor(resume, jobPosting).Where(r => r.Section == SectionType.Skills).ToList();

        advice.Should().HaveCount(2, "C# is matched; Go and Rust are not");
        advice.Should().OnlyContain(r => r.Impact == 0.0,
            "the section is pinned at its neutral score, so no fix can move it");

        advice.Single(r => r.Kind == RecommendationKind.MissingMustHaveSkill)
            .Priority.Should().Be(RecommendationPriority.Critical, "the posting still screens on it");
        advice.Single(r => r.Kind == RecommendationKind.MissingNiceToHaveSkill)
            .Priority.Should().Be(RecommendationPriority.NiceToHave, "nothing gates it and it is worth nothing");
    }

    [Fact]
    public void Recommendations_AreReturnedInATotalDeterministicOrder()
    {
        var resume = BuildThoroughlyImperfectResume();
        var jobPosting = BuildThoroughlyDemandingPosting();

        var advice = AdviceFor(resume, jobPosting);

        advice.Should().HaveCountGreaterThan(1);
        advice.Should().BeInAscendingOrder(RecommendationOrder.Display);

        // And scoring the same inputs twice gives the same sequence, message for message. The order has
        // to be a function of the content — anything resolved by chance would put a different tenth
        // recommendation in front of the candidate on a refresh.
        AdviceFor(BuildThoroughlyImperfectResume(), BuildThoroughlyDemandingPosting())
            .Select(r => r.Message)
            .Should().Equal(advice.Select(r => r.Message));
    }

    // Ten is a reading limit. Past it a candidate is handed a backlog rather than advice, and the total
    // order is what makes the ten that survive the ten worth most.
    //
    // The empty resume against fifteen unmatched must-haves produces EIGHTEEN pieces of advice, measured
    // by raising the cap and counting rather than by adding them up in prose: sixteen Critical (fifteen
    // skills at 0.45/15 = 0.03 each, gated Critical by the must-have rule, plus the missing education at
    // 0.10), one Important (certifications) and one NiceToHave (projects). The ten kept are the
    // highest-impact Critical followed by nine skills — and WHICH nine is decided by the message
    // tiebreak, the one part of the total order nothing else in this file ties hard enough to reach.
    [Fact]
    public void Recommendations_AreCappedAtTen()
    {
        var resume = BuildResume();
        var jobPosting = JobPosting.Create(AccountId.New(), "Backend Developer", OrganizationName.Create("Acme"));

        // Added in DESCENDING name order on purpose. The builder walks the requirements in the order the
        // posting holds them and Order is a stable sort, so with ascending insertion the expected
        // sequence below would hold even with the message tiebreak deleted — a negative control proved
        // exactly that. Reversed, insertion order and message order disagree, and only the comparer can
        // reconcile them.
        foreach (var index in Enumerable.Range(1, 15).Reverse())
            jobPosting.AddRequirement(
                JobRequirement.Create(Technology.Create($"skill-{index:00}"), RequirementPriority.MustHave));

        var advice = AdviceFor(resume, jobPosting);

        advice.Should().HaveCount(10);
        advice.Should().BeInAscendingOrder(RecommendationOrder.Display);

        advice[0].Kind.Should().Be(RecommendationKind.NoEducationRecorded,
            "0.10 is the biggest Critical impact here, and Impact is descending within a priority");

        advice.Skip(1).Should().OnlyContain(r => r.Section == SectionType.Skills);
        advice.Skip(1).Select(r => SkillNameIn(r.Message)).Should().Equal(
            "skill-01", "skill-02", "skill-03", "skill-04", "skill-05",
            "skill-06", "skill-07", "skill-08", "skill-09");

        advice.Should().NotContain(r => r.Priority != RecommendationPriority.Critical,
            "sixteen Critical items leave no room for the certifications and projects advice below them");
    }

    private static string SkillNameIn(string message) => message.Split('\'')[1];

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

    private static JobPosting WithLanguages(
        JobPosting jobPosting, params (string Name, LanguageProficiency Minimum)[] languages)
    {
        foreach (var (name, minimum) in languages)
            jobPosting.AddLanguageRequirement(LanguageRequirement.Create(name, minimum));
        return jobPosting;
    }

    // A resume with a real gap in every one of the six sections, so a per-section assertion has
    // something to measure everywhere instead of passing on an empty subset.
    private static Resume BuildThoroughlyImperfectResume()
    {
        var resume = BuildResume("C#");
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-2), ReferenceDate.AddYears(-1))));
        resume.AddExperience(new Experience(
            ExperienceType.Volunteer, OrganizationName.Create("Charity"), "Mentor",
            DateRange.Create(ReferenceDate.AddYears(-1), ReferenceDate)));
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-8), ReferenceDate.AddYears(-4)), null));
        resume.AddCertificate(new Certificate("Cert A", OrganizationName.Create("Amazon"), null, null, null));
        resume.AddProject(new Project("Project A", DateRange.Create(ReferenceDate.AddYears(-2)))
        {
            Technologies = [Technology.Create("terraform")],
        });
        resume.AddLanguage(new Language("English", "Some school English", LanguageProficiency.Basic));
        resume.AddLanguage(new Language("German", "Bilingue", Level: null));
        return resume;
    }

    private static JobPosting BuildThoroughlyDemandingPosting() =>
        WithLanguages(
            BuildJobPosting(
                ("C#", RequirementPriority.MustHave),
                ("SQL", RequirementPriority.MustHave),
                ("Redis", RequirementPriority.NiceToHave)),
            ("English", LanguageProficiency.Professional),
            ("German", LanguageProficiency.Professional),
            ("French", LanguageProficiency.Basic));

    private static Resume BuildPerfectResume()
    {
        var resume = BuildResume("C#");
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-6))));
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), "BSc", "Computer Science",
            DateRange.Create(ReferenceDate.AddYears(-10), ReferenceDate.AddYears(-6)), null));
        foreach (var issuer in new[] { "Amazon", "Microsoft", "Google" })
            resume.AddCertificate(new Certificate($"Cert {issuer}", OrganizationName.Create(issuer), null, null, null));
        foreach (var name in new[] { "Project A", "Project B", "Project C" })
            resume.AddProject(new Project(name, DateRange.Create(ReferenceDate.AddYears(-1)))
            {
                Technologies = [Technology.Create("terraform")],
            });
        resume.AddLanguage(new Language("English", "Native speaker", LanguageProficiency.Native));
        return resume;
    }
}

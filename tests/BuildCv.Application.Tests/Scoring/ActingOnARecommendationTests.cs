using BuildCv.Application.Scoring;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Application.Tests.Scoring;

// THE claim this whole feature rests on, measured rather than argued:
//
//     Impact is the increase in WeightedTotal the candidate would gain by acting on this advice.
//
// Every test here scores a resume, takes one recommendation off the result, applies EXACTLY the fix
// that recommendation names, re-scores, and asserts the difference between the two totals equals the
// Impact the candidate was shown. Nothing is compared against a hand-written expected number: the
// engine is asked twice and the two answers have to agree with the promise made between them.
//
// A rule whose Impact cannot be measured this way does not have a derived impact, it has a plausible
// one — and a plausible number is worse than none, because it gives a candidate precision they cannot
// check about the one thing they are here to improve.
public class ActingOnARecommendationTests
{
    private static readonly DateOnly ReferenceDate = new(2025, 1, 1);

    // EMPTY LEXICON, and every assertion in this file is unchanged beside it. That is the evidence that
    // consulting a skill lexicon reproduces the previous behaviour bit for bit: Canonicalize is the
    // identity on an empty table, so ScoringRules.IsSatisfiedBy collapses to the whole-string comparison
    // it was. EmptyLexiconEquivalenceTests makes the same claim over a vocabulary chosen to break it.
    private readonly ScoringEngine _engine = new(FakeSkillLexicon.Empty);

    // 1e-9 rather than exact equality. The engine sums six weighted terms and the builder evaluates one
    // section formula twice; the two paths reach the same value through different additions, so the
    // last bits are free to differ. A tolerance of 1e-9 is nine orders of magnitude tighter than the
    // 0.01 that would matter to a candidate, and still far looser than the ~1e-16 the arithmetic
    // actually costs.
    private const double Tolerance = 1e-9;

    // EVERY SCENARIO BELOW LEAVES A GAP BEHIND, and that is not incidental.
    //
    // Four of these tests originally closed their section completely, and a negative control walked
    // straight through two of them: an impact computed as "matched + TOTAL weight" instead of
    // "matched + THIS requirement's weight" is clamped back to the same 1.0 whenever the fix would have
    // reached the cap anyway, so the wrong formula and the right one agreed. A scenario that only
    // partially closes its section is what makes the two answers different numbers.
    private void ActingOnTheAdviceOf(
        RecommendationKind kind, Resume resume, JobPosting jobPosting, Action<Resume> applyTheFix)
    {
        var before = _engine.Score(resume, jobPosting, ReferenceDate);
        var advice = before.Recommendations.Should().ContainSingle(r => r.Kind == kind).Subject;

        // Without this the test would pass for a rule that promised nothing and a fix that changed
        // nothing: 0 - 0 == 0. It is the assertion that makes the one below mean something.
        advice.Impact.Should().BeGreaterThan(0.0, "an impact of zero makes the delta assertion vacuous");

        applyTheFix(resume);

        var after = _engine.Score(resume, jobPosting, ReferenceDate);

        (after.WeightedTotal - before.WeightedTotal).Should().BeApproximately(advice.Impact, Tolerance,
            "acting on the advice must pay exactly what the advice promised");
    }

    // One matched must-have, one unmatched must-have and one unmatched nice-to-have: weights 1.0, 1.0
    // and 0.5, so the section sits at 0.4 and adding SQL takes it to 0.8, not to the cap.
    [Fact]
    public void ActingOnAMissingMustHaveSkill_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");

        ActingOnTheAdviceOf(
            RecommendationKind.MissingMustHaveSkill, resume, BuildPartiallyMatchedPosting(),
            r => r.AddSkill(Skill.Create(Technology.Create("SQL"))));
    }

    [Fact]
    public void ActingOnAMissingNiceToHaveSkill_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");

        ActingOnTheAdviceOf(
            RecommendationKind.MissingNiceToHaveSkill, resume, BuildPartiallyMatchedPosting(),
            r => r.AddSkill(Skill.Create(Technology.Create("Redis"))));
    }

    [Fact]
    public void ActingOnAMissingCertification_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");
        resume.AddCertificate(new Certificate("Cert A", OrganizationName.Create("Amazon"), null, null, null));

        ActingOnTheAdviceOf(
            RecommendationKind.FewerCertificationsThanExpected, resume, BuildJobPosting(),
            r => r.AddCertificate(new Certificate("Cert B", OrganizationName.Create("Google"), null, null, null)));
    }

    [Fact]
    public void ActingOnNoEducationRecorded_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");

        ActingOnTheAdviceOf(
            RecommendationKind.NoEducationRecorded, resume, BuildJobPosting(),
            r => r.AddEducation(new Education(
                OrganizationName.Create("MIT"), "BSc", "Computer Science",
                DateRange.Create(ReferenceDate.AddYears(-8), ReferenceDate.AddYears(-4)), null)));
    }

    [Fact]
    public void ActingOnNoDegreeRecorded_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");
        resume.AddEducation(new Education(
            OrganizationName.Create("MIT"), null, null,
            DateRange.Create(ReferenceDate.AddYears(-8), ReferenceDate.AddYears(-4)), null));

        ActingOnTheAdviceOf(
            RecommendationKind.NoDegreeRecorded, resume, BuildJobPosting(),
            r => r.AddEducation(new Education(
                OrganizationName.Create("MIT"), "BSc", "Computer Science",
                DateRange.Create(ReferenceDate.AddYears(-8), ReferenceDate.AddYears(-4)), null)));
    }

    // The added project deliberately lists a technology the posting does NOT require. Project
    // technologies also satisfy skill requirements, so a matching name would move two sections at once
    // and the delta would exceed the projects impact through no fault of the rule.
    [Fact]
    public void ActingOnTooFewProjects_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");
        resume.AddProject(new Project("Project A", DateRange.Create(ReferenceDate.AddYears(-2)))
        {
            Technologies = [Technology.Create("terraform")],
        });

        ActingOnTheAdviceOf(
            RecommendationKind.FewerProjectsThanExpected, resume,
            BuildJobPosting(("C#", RequirementPriority.MustHave)),
            r => r.AddProject(new Project("Project B", DateRange.Create(ReferenceDate.AddYears(-1)))
            {
                Technologies = [Technology.Create("terraform")],
            }));
    }

    // Two stated languages, both unsatisfied for different reasons, so closing one takes the section
    // from 0.0 to 0.5 rather than to its cap.
    [Fact]
    public void ActingOnAMissingLanguage_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");
        resume.AddLanguage(Language.Create("German", "A little", LanguageProficiency.Basic));
        var jobPosting = WithLanguages(
            BuildJobPosting(),
            ("English", LanguageProficiency.Professional),
            ("German", LanguageProficiency.Professional));

        ActingOnTheAdviceOf(
            RecommendationKind.LanguageMissing, resume, jobPosting,
            r => r.AddLanguage(Language.Create("English", "Working proficiency", LanguageProficiency.Professional)));
    }

    // Why LanguageMissing names the required level in its INSTRUCTION and not only in the clause after
    // it. The same scenario as above, acting on the shorter reading of the advice -- add the language,
    // say nothing about the level -- and the promised impact is not paid: the gap moves from Missing to
    // LevelNotRecorded, which still does not satisfy the requirement. Executed here so the wording is
    // pinned by a consequence rather than by a string comparison, which would break on any rephrasing
    // and prove nothing about what the candidate gets.
    [Fact]
    public void AddingAMissingLanguageWithoutALevel_PaysNothingAndBecomesADifferentGap()
    {
        var resume = BuildResume("C#");
        resume.AddLanguage(Language.Create("German", "A little", LanguageProficiency.Basic));
        var jobPosting = WithLanguages(
            BuildJobPosting(),
            ("English", LanguageProficiency.Professional),
            ("German", LanguageProficiency.Professional));

        var before = _engine.Score(resume, jobPosting, ReferenceDate);
        var advice = before.Recommendations
            .Should().ContainSingle(r => r.Kind == RecommendationKind.LanguageMissing).Subject;
        advice.Impact.Should().BeGreaterThan(0.0, "the advice promises a real gain");

        resume.AddLanguage(Language.Create("English", "Bilingue", level: null));

        var after = _engine.Score(resume, jobPosting, ReferenceDate);

        (after.WeightedTotal - before.WeightedTotal).Should().Be(0.0,
            "a language held with no recorded level satisfies nothing, so the shorter reading of the "
            + "advice pays none of what it promised");
        after.Recommendations.Should().Contain(r => r.Kind == RecommendationKind.LanguageLevelNotRecorded);
    }

    [Fact]
    public void ActingOnALanguageBelowTheRequiredLevel_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");
        var held = Language.Create("English", "Some school English", LanguageProficiency.Basic);
        resume.AddLanguage(held);
        var jobPosting = WithLanguages(
            BuildJobPosting(),
            ("English", LanguageProficiency.Professional),
            ("German", LanguageProficiency.Basic));

        ActingOnTheAdviceOf(
            RecommendationKind.LanguageBelowRequiredLevel, resume, jobPosting,
            r =>
            {
                r.RemoveLanguage(held);
                r.AddLanguage(held with { Level = LanguageProficiency.Fluent });
            });
    }

    // The rule that exists because of what it REFUSES to do. The candidate already speaks the language;
    // what is missing is a field, and the free-text fluency beside it is never parsed into one. So the
    // gap is reported as advice with a number on it rather than deducted in silence.
    [Fact]
    public void ActingOnALanguageWithNoRecordedLevel_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");
        var held = Language.Create("English", "Bilingue", level: null);
        resume.AddLanguage(held);
        var jobPosting = WithLanguages(
            BuildJobPosting(),
            ("English", LanguageProficiency.Professional),
            ("French", LanguageProficiency.Basic));

        ActingOnTheAdviceOf(
            RecommendationKind.LanguageLevelNotRecorded, resume, jobPosting,
            r =>
            {
                r.RemoveLanguage(held);
                r.AddLanguage(held with { Level = LanguageProficiency.Native });
            });
    }

    // The only experience rule there is. "Have more years of experience" is a fact about time and gets
    // no recommendation at all; a mis-tagged entry is something a candidate can fix in one edit, and
    // its impact is exactly the days it would add divided by the five-year cap.
    [Fact]
    public void ActingOnAnExperienceNotMarkedProfessional_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = BuildResume("C#");
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-2), ReferenceDate.AddYears(-1))));
        var misTagged = new Experience(
            ExperienceType.Volunteer, OrganizationName.Create("Contoso"), "Backend Developer",
            DateRange.Create(ReferenceDate.AddYears(-1), ReferenceDate));
        resume.AddExperience(misTagged);

        ActingOnTheAdviceOf(
            RecommendationKind.ExperienceNotMarkedProfessional, resume, BuildJobPosting(),
            r =>
            {
                r.RemoveExperience(misTagged);
                r.AddExperience(misTagged with { Type = ExperienceType.Professional });
            });
    }

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

    // Weights 1.0 (matched), 1.0 and 0.5. Total 2.5, matched 1.0, so the section scores 0.4 and closing
    // either gap lands strictly between there and the cap. Exactly one recommendation of each skill
    // kind, so the helper's ContainSingle is unambiguous.
    private static JobPosting BuildPartiallyMatchedPosting() =>
        BuildJobPosting(
            ("C#", RequirementPriority.MustHave),
            ("SQL", RequirementPriority.MustHave),
            ("Redis", RequirementPriority.NiceToHave));
}

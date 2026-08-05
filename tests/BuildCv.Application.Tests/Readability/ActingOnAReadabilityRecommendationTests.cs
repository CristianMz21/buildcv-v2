using BuildCv.Application.Readability;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Readability;

// THE claim this whole feature rests on, measured rather than argued:
//
//     Impact is the increase in WeightedTotal the candidate would gain by acting on this advice.
//
// Every test here evaluates a resume, takes one recommendation off the result, applies EXACTLY the fix
// that recommendation names, re-evaluates, and asserts the difference between the two totals equals the
// Impact the candidate was shown. Nothing is compared against a hand-written expected number: the engine
// is asked twice and the two answers have to agree with the promise made between them.
//
// A rule whose Impact cannot be measured this way does not have a derived impact, it has a plausible
// one -- and a plausible number is worse than none, because it gives a candidate precision they cannot
// check about the one thing they are here to improve.
//
// IT IS ALSO WHAT PINS THE DISJOINTNESS of the four sections. Impact is computed as one section's weight
// times that section's delta, so a fix that moved a second section would make the total delta larger
// than the number promised and these assertions would fail -- which is why the work-history heading
// lives in Chronology rather than in Completeness.
public class ActingOnAReadabilityRecommendationTests
{
    private static readonly DateOnly ReferenceDate = ReadabilityTestResumes.ReferenceDate;

    private readonly ReadabilityEngine _engine = new();

    // 1e-9 rather than exact equality. The engine sums five weighted terms and the builder evaluates one
    // section formula twice; the two paths reach the same value through different additions, so the last
    // bits are free to differ. Nine orders of magnitude tighter than the 0.01 that would matter to a
    // candidate, and still far looser than the ~1e-16 the arithmetic actually costs.
    private const double Tolerance = 1e-9;

    // WHERE A SCENARIO CAN LEAVE A GAP BEHIND, IT DOES. A fix that takes its section all the way to the
    // ceiling cannot tell the right formula from a wrong one that also clamps to 1.0, so the three
    // scenarios with room to spare -- the two per-bullet-point rules and the employment gap -- are built
    // to land strictly between where they started and the cap. Three cannot: "add your first education
    // section", "add your work history" and "write your first bullet point" close the only gap their
    // section has, and there is no partial version of them to construct.
    private void ActingOnTheAdviceOf(
        ReadabilityRecommendationKind kind, Resume resume, Action<Resume> applyTheFix)
    {
        var before = _engine.Evaluate(resume, ReferenceDate);
        var advice = before.Recommendations.Should().ContainSingle(r => r.Kind == kind).Subject;

        // Without this the test would pass for a rule that promised nothing and a fix that changed
        // nothing: 0 - 0 == 0. It is the assertion that makes the one below mean something.
        advice.Impact.Should().BeGreaterThan(0.0, "an impact of zero makes the delta assertion vacuous");

        applyTheFix(resume);

        var after = _engine.Evaluate(resume, ReferenceDate);

        (after.WeightedTotal - before.WeightedTotal).Should().BeApproximately(advice.Impact, Tolerance,
            "acting on the advice must pay exactly what the advice promised");
    }

    // Completeness. All three sections are missing, so the fix takes the section to one third and not to
    // its ceiling: a formula that jumped straight to 1.0 would pay three times what it promised.
    [Fact]
    public void ActingOnNoEducationRecorded_RaisesTheScoreByExactlyItsImpact()
    {
        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.NoEducationRecorded,
            ReadabilityTestResumes.Empty(),
            resume => resume.AddEducation(new Education(
                OrganizationName.Create("Universidad de Buenos Aires"),
                "Ingeniería en Sistemas", "Computer Science",
                DateRange.Create(new DateOnly(2013, 3, 1), new DateOnly(2018, 12, 1)), null)));
    }

    [Fact]
    public void ActingOnNoSkillsRecorded_RaisesTheScoreByExactlyItsImpact()
    {
        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.NoSkillsRecorded,
            ReadabilityTestResumes.Empty(),
            resume => resume.AddSkill(Skill.Create(Technology.Create("C#"))));
    }

    [Fact]
    public void ActingOnNoProfessionalSummary_RaisesTheScoreByExactlyItsImpact()
    {
        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.NoProfessionalSummary,
            ReadabilityTestResumes.Empty(),
            resume => resume.UpdateContactInformation(
                resume.ContactInformation with { Summary = "Backend engineer, eight years on payments." }));
    }

    // Contact. Same shape: all three channels are missing, so each fix lands on one third.
    [Fact]
    public void ActingOnNoPhoneNumber_RaisesTheScoreByExactlyItsImpact()
    {
        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.NoPhoneNumber,
            ReadabilityTestResumes.Empty(),
            resume => resume.UpdateContactInformation(
                resume.ContactInformation with { PhoneNumber = PhoneNumber.Create("+541155501234") }));
    }

    [Fact]
    public void ActingOnNoLocation_RaisesTheScoreByExactlyItsImpact()
    {
        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.NoLocation,
            ReadabilityTestResumes.Empty(),
            resume => resume.UpdateContactInformation(
                resume.ContactInformation with { Location = "Buenos Aires, Argentina" }));
    }

    [Fact]
    public void ActingOnNoOnlinePresence_RaisesTheScoreByExactlyItsImpact()
    {
        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.NoOnlinePresence,
            ReadabilityTestResumes.Empty(),
            resume => resume.UpdateContactInformation(
                resume.ContactInformation with { Website = Url.Create("https://janedoe.dev") }));
    }

    // Chronology, first gap: there is no work history at all. The fix adds ONE role with no bullet
    // points, which is exactly what the advice names -- adding one with highlights would move
    // Achievements too and the delta would exceed the promise through no fault of the rule.
    [Fact]
    public void ActingOnNoExperienceRecorded_RaisesTheScoreByExactlyItsImpact()
    {
        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.NoExperienceRecorded,
            ReadabilityTestResumes.Empty(),
            resume => resume.AddExperience(Role("Acme", "Backend Developer", 2022, 2024)));
    }

    // Achievements, first gap: roles are listed and say nothing. The advice names BOTH halves of the
    // section -- start with a verb AND state a number -- so the fix is a bullet point that does both,
    // and that is what makes the promised impact payable in full.
    [Fact]
    public void ActingOnNoExperienceHighlights_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = ReadabilityTestResumes.Empty();
        var role = Role("Acme", "Backend Developer", 2022, 2024);
        resume.AddExperience(role);

        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.NoExperienceHighlights,
            resume,
            r => Replace(r, role, role with { Highlights = ["Reduced checkout latency by 40%"] }));
    }

    // The shorter reading of that advice, executed so the wording is pinned by a CONSEQUENCE rather than
    // by a string comparison. A first bullet point that starts with a verb and states no number pays
    // exactly half of what was promised -- which is why the sentence names both halves.
    [Fact]
    public void AddingAFirstHighlightWithoutANumber_PaysOnlyHalfOfWhatTheAdvicePromised()
    {
        var resume = ReadabilityTestResumes.Empty();
        var role = Role("Acme", "Backend Developer", 2022, 2024);
        resume.AddExperience(role);

        var before = _engine.Evaluate(resume, ReferenceDate);
        var advice = before.Recommendations
            .Should().ContainSingle(r => r.Kind == ReadabilityRecommendationKind.NoExperienceHighlights).Subject;

        Replace(resume, role, role with { Highlights = ["Reduced checkout latency"] });

        var after = _engine.Evaluate(resume, ReferenceDate);

        (after.WeightedTotal - before.WeightedTotal).Should().BeApproximately(advice.Impact / 2.0, Tolerance,
            "a bullet point with no number satisfies one of the two halves the advice named");
        after.Recommendations.Should()
            .Contain(r => r.Kind == ReadabilityRecommendationKind.HighlightWithoutANumber);
    }

    // THREE bullet points, one of them quantified, so putting a number in a second takes the section
    // from 0.667 to 0.833 rather than to its cap. The replacement keeps the same leading verb, so
    // nothing but the quantified count moves.
    [Fact]
    public void ActingOnAHighlightWithoutANumber_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = ReadabilityTestResumes.Empty();
        var role = Role("Acme", "Backend Developer", 2022, 2024) with
        {
            Highlights = ["Reduced checkout latency by 40%", "Migrated the billing service", "Led the payments team"],
        };
        resume.AddExperience(role);

        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.HighlightWithoutANumber,
            resume,
            r => Replace(r, role, role with
            {
                Highlights =
                [
                    "Reduced checkout latency by 40%",
                    "Migrated the billing service in 3 months",
                    "Led the payments team",
                ],
            }));
    }

    // The mirror image, and the replacement keeps the number so nothing but the action-verb count moves.
    [Fact]
    public void ActingOnAHighlightWithoutAnActionVerb_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = ReadabilityTestResumes.Empty();
        var role = Role("Acme", "Backend Developer", 2022, 2024) with
        {
            Highlights =
            [
                "Reduced checkout latency by 40%",
                "Responsible for 3 payment integrations",
                "Owner of 2 internal libraries",
            ],
        };
        resume.AddExperience(role);

        ActingOnTheAdviceOf(
            ReadabilityRecommendationKind.HighlightWithoutAnActionVerb,
            resume,
            r => Replace(r, role, role with
            {
                Highlights =
                [
                    "Reduced checkout latency by 40%",
                    "Maintained 3 payment integrations",
                    "Owner of 2 internal libraries",
                ],
            }));
    }

    // THE RICHEST OF THE ELEVEN, and the one whose arithmetic is easiest to get wrong. Closing an
    // employment gap by adding an entry moves BOTH terms of the formula: the entry count rises by one
    // and the broken transition becomes two unbroken ones. Two gaps are left standing before the fix and
    // one after, so the section lands strictly between where it started and its cap -- a formula that
    // restated the gain as 1/n, or that forgot the denominator moves, would answer a different number.
    [Fact]
    public void ActingOnAnUnexplainedEmploymentGap_RaisesTheScoreByExactlyItsImpact()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(Role("Acme", "Junior Developer", 2018, 2019));
        resume.AddExperience(Role("Globex", "Developer", 2021, 2022));
        resume.AddExperience(Role("Initech", "Senior Developer", 2023, 2024));

        var before = _engine.Evaluate(resume, ReferenceDate);

        // The advice naming the SECOND role, which is the gap the fix below bridges. Both gaps carry the
        // same impact, so taking the wrong one would still pass -- selecting by name is what makes the
        // fix and the promise describe the same break.
        var advice = before.Recommendations.Should()
            .ContainSingle(r => r.Kind == ReadabilityRecommendationKind.UnexplainedEmploymentGap
                && r.Message.Contains("'Developer'", StringComparison.Ordinal)).Subject;
        advice.Impact.Should().BeGreaterThan(0.0);

        resume.AddExperience(Role("Contractor SRL", "Freelance Developer", 2019, 2021));

        var after = _engine.Evaluate(resume, ReferenceDate);

        (after.WeightedTotal - before.WeightedTotal).Should().BeApproximately(advice.Impact, Tolerance,
            "acting on the advice must pay exactly what the advice promised");

        after.Recommendations.Should()
            .ContainSingle(r => r.Kind == ReadabilityRecommendationKind.UnexplainedEmploymentGap,
                "the other break is still there and still has advice of its own");
    }

    private static Experience Role(string organization, string position, int startYear, int endYear) =>
        new(ExperienceType.Professional,
            OrganizationName.Create(organization),
            position,
            DateRange.Create(new DateOnly(startYear, 1, 1), new DateOnly(endYear, 1, 1)));

    // Experience is a record, so "edit this entry" is remove-and-add. The period is unchanged in every
    // caller, which is what keeps Chronology out of the Achievements deltas.
    private static void Replace(Resume resume, Experience original, Experience edited)
    {
        resume.RemoveExperience(original);
        resume.AddExperience(edited);
    }
}

using BuildCv.Application.Readability;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Application.Tests.Readability;

// WHAT IS EMITTED, and — more interestingly — what is not. What each piece of advice is WORTH is
// ActingOnAReadabilityRecommendationTests.
public class ReadabilityRecommendationBuilderTests
{
    private static readonly DateOnly ReferenceDate = ReadabilityTestResumes.ReferenceDate;

    private readonly ReadabilityEngine _engine = new();

    private IReadOnlyList<ReadabilityRecommendation> AdviceFor(Resume resume) =>
        _engine.Evaluate(resume, ReferenceDate).Recommendations;

    // EMIT NOTHING A CANDIDATE CANNOT ACT ON. "Add a bullet point" names an edit to a role that does not
    // exist, so a resume with no work history gets no Achievements advice at all — even though the
    // section scores zero and carries a quarter of the weight. The advice appears once the work history
    // does, which the second half of this test executes.
    [Fact]
    public void A_resume_with_no_experience_gets_no_achievements_advice_until_it_has_a_role()
    {
        var resume = ReadabilityTestResumes.Empty();

        AdviceFor(resume).Should().NotContain(r => r.Section == ReadabilitySectionType.Achievements,
            "there is no role to add a bullet point to");

        resume.AddExperience(Role("Acme", "Backend Developer", 2022, 2024));

        AdviceFor(resume).Should()
            .Contain(r => r.Kind == ReadabilityRecommendationKind.NoExperienceHighlights);
    }

    // The section still scores zero and still carries its weight while that advice is absent. Stating it
    // as an assertion because the alternative — renormalizing Achievements out for a resume with no
    // roles — would hand a candidate a HIGHER total for writing less.
    [Fact]
    public void The_achievements_section_still_scores_zero_and_carries_weight_with_no_experience()
    {
        var breakdown = _engine.Evaluate(ReadabilityTestResumes.Empty(), ReferenceDate).Breakdown;

        breakdown.AchievementsScore.Should().Be(0.0);
        breakdown.Weights.Achievements.Should().BeGreaterThan(0.0);
    }

    // The emptiest resume the Domain can hold produces the seven pieces of advice it can act on: three
    // sections, three contact channels, and the work history. Pinned as an exact set so a rule that
    // stopped firing is a failure rather than a silently shorter list.
    [Fact]
    public void An_empty_resume_gets_advice_for_every_gap_it_can_act_on()
    {
        AdviceFor(ReadabilityTestResumes.Empty()).Select(r => r.Kind).Should().BeEquivalentTo(
        [
            ReadabilityRecommendationKind.NoEducationRecorded,
            ReadabilityRecommendationKind.NoSkillsRecorded,
            ReadabilityRecommendationKind.NoProfessionalSummary,
            ReadabilityRecommendationKind.NoPhoneNumber,
            ReadabilityRecommendationKind.NoLocation,
            ReadabilityRecommendationKind.NoOnlinePresence,
            ReadabilityRecommendationKind.NoExperienceRecorded,
        ]);
    }

    // ONE recommendation per bullet-point rule, not one per offending line. The advice is "do this to
    // one more of them", and its impact is what one more is worth — five copies of the same sentence
    // would be five chances to act on one piece of advice, and only the first would pay.
    [Fact]
    public void The_bullet_point_rules_emit_one_recommendation_each_however_many_lines_offend()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(Role("Acme", "Backend Developer", 2022, 2024) with
        {
            Highlights = ["Worked on payments", "Worked on billing", "Worked on refunds"],
        });

        var advice = AdviceFor(resume);

        advice.Should().ContainSingle(r => r.Kind == ReadabilityRecommendationKind.HighlightWithoutANumber);
        advice.Should().ContainSingle(r => r.Kind == ReadabilityRecommendationKind.HighlightWithoutAnActionVerb);
    }

    // One per gap, because a candidate deciding where to spend an afternoon needs them apart — and
    // because "add an entry covering the gap" is useless without saying which one.
    [Fact]
    public void Each_employment_gap_gets_its_own_advice_naming_the_role_that_follows_it()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(Role("Acme", "Junior Developer", 2018, 2019));
        resume.AddExperience(Role("Globex", "Developer", 2021, 2022));
        resume.AddExperience(Role("Initech", "Staff Engineer", 2023, 2024));

        var gaps = AdviceFor(resume)
            .Where(r => r.Kind == ReadabilityRecommendationKind.UnexplainedEmploymentGap)
            .ToList();

        gaps.Should().HaveCount(2);
        gaps.Should().Contain(r => r.Message.Contains("'Developer'", StringComparison.Ordinal));
        gaps.Should().Contain(r => r.Message.Contains("'Staff Engineer'", StringComparison.Ordinal));
    }

    // PRIORITY IS A PURE FUNCTION OF IMPACT, so the label and the number can never disagree. Asserted
    // over the whole rule set rather than per rule: hand-assigned priorities are how a rule set drifts
    // into "everything is Critical", and this is the assertion that would catch the first one.
    [Fact]
    public void Priority_is_decided_by_impact_alone_across_every_rule()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(Role("Acme", "Junior Developer", 2018, 2019));
        resume.AddExperience(Role("Globex", "Developer", 2021, 2022));

        foreach (var advice in AdviceFor(resume))
        {
            var expected = advice.Impact >= 0.10 ? RecommendationPriority.Critical
                : advice.Impact >= 0.03 ? RecommendationPriority.Important
                : RecommendationPriority.NiceToHave;

            advice.Priority.Should().Be(expected,
                "{0} carries an impact of {1}", advice.Kind, advice.Impact);
        }
    }

    // Ten is a reading limit, not a scoring one: past it a candidate is handed a backlog rather than
    // advice. Reached with employment gaps, which is the only rule that can emit an unbounded number.
    [Fact]
    public void No_more_than_ten_recommendations_survive_however_many_rules_fire()
    {
        var resume = ReadabilityTestResumes.Empty();

        // Twelve roles, each two years after the last ended: eleven breaks, plus the six section and
        // contact gaps and the missing bullet points.
        for (var index = 0; index < 12; index++)
            resume.AddExperience(Role("Acme", $"Developer {index}", 1990 + (index * 3), 1991 + (index * 3)));

        AdviceFor(resume).Should().HaveCount(10);
    }

    // The order the ten survivors are chosen by, and the order they are read in. Critical first, then
    // biggest win within a priority — the same total order the Api applies again on the way out.
    [Fact]
    public void Advice_comes_back_sorted_by_priority_then_by_impact()
    {
        var advice = AdviceFor(ReadabilityTestResumes.Empty());

        advice.Should().BeInAscendingOrder(r => r.Priority);
        foreach (var group in advice.GroupBy(r => r.Priority))
            group.Should().BeInDescendingOrder(r => r.Impact);
    }

    // Every message has to be something a person can DO. Checked structurally rather than by asserting
    // sentences — a string comparison would break on any rephrasing and prove nothing about what the
    // candidate gets — but a blank or a bare noun phrase would be caught here.
    [Fact]
    public void Every_message_is_a_non_empty_instruction()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(Role("Acme", "Junior Developer", 2018, 2019));
        resume.AddExperience(Role("Globex", "Developer", 2021, 2022));

        foreach (var advice in AdviceFor(resume))
        {
            advice.Message.Should().NotBeNullOrWhiteSpace();
            advice.Message.Trim().Should().Be(advice.Message);
            advice.Impact.Should().BeInRange(0.0, 1.0);
        }
    }

    private static Experience Role(string organization, string position, int startYear, int endYear) =>
        new(ExperienceType.Professional,
            OrganizationName.Create(organization),
            position,
            DateRange.Create(new DateOnly(startYear, 1, 1), new DateOnly(endYear, 1, 1)));
}

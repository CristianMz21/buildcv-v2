using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

// NaN and the infinities, at every gate in this folder that validates a number.
//
// They all shared one hole: EVERY comparison against NaN is false, so `NaN < 0` and `NaN > 1` both fail
// and a NaN sailed through a range check untouched. `ScoringWeightsSnapshot` had it twice over, since
// `Math.Abs(NaN - 1.0) > 0.0001` is false as well — a NaN weight passed the sum invariant too.
//
// It stopped being theoretical when renormalization introduced a division into the weights path: a
// weight is now a share, and a score is a share, and an Impact is a weight times a score delta. A NaN
// reaching ScoreBreakdown poisons WeightedTotal, every band and the entire response; a NaN reaching
// Recommendation is advice worth an unknowable amount that sorts arbitrarily against everything else.
public class NonFiniteScoringInputTests
{
    private static readonly ScoringWeightsSnapshot Weights = ScoringWeightsSnapshot.Default();

    public static TheoryData<double> NonFinite => new() { double.NaN, double.PositiveInfinity, double.NegativeInfinity };

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void ScoreBreakdown_rejects_a_non_finite_score(double value)
    {
        var act = () => ScoreBreakdown.Create(value, 0.0, 0.0, 0.0, 0.0, 0.0, Weights);

        act.Should().Throw<ArgumentException>().WithParameterName("skillsScore");
    }

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void SectionScore_rejects_a_non_finite_score(double value)
    {
        var act = () => SectionScore.Create(SectionType.Skills, value, 0.45);

        act.Should().Throw<ArgumentException>().WithParameterName("score");
    }

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void SectionScore_rejects_a_non_finite_weight(double value)
    {
        var act = () => SectionScore.Create(SectionType.Skills, 0.5, value);

        act.Should().Throw<ArgumentException>().WithParameterName("weight");
    }

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void Recommendation_rejects_a_non_finite_impact(double value)
    {
        var act = () => Recommendation.Create(
            SectionType.Skills, RecommendationPriority.Critical, RecommendationKind.MissingMustHaveSkill,
            "Add SQL.", value);

        act.Should().Throw<InvalidRecommendationException>();
    }

    // NaN specifically, and with the OTHER five weights summing to 1.0 already, so the only thing wrong
    // with this payload is the NaN. Without the finiteness guard it passes both the non-negative check
    // and the sum check and is persisted as a snapshot whose arithmetic can never be reproduced.
    [Fact]
    public void ScoringWeightsSnapshot_rejects_a_NaN_weight_that_the_sum_check_cannot_see()
    {
        (Math.Abs(double.NaN - 1.0) > 0.0001).Should().BeFalse(
            "the sum invariant is a > comparison, and every comparison against NaN is false — which is "
            + "why it cannot reject a NaN weight on its own");

        var act = () => ScoringWeightsSnapshot.Create(0.45, 0.20, 0.10, 0.10, 0.05, double.NaN);

        act.Should().Throw<ArgumentException>().WithMessage("*finite*");
    }

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void ScoringWeightsSnapshot_rejects_any_non_finite_weight(double value)
    {
        var act = () => ScoringWeightsSnapshot.Create(value, 0.20, 0.10, 0.10, 0.05, 0.10);

        act.Should().Throw<ArgumentException>();
    }
}

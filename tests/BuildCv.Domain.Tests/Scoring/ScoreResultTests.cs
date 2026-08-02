using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

// ScoringEngine.Score is the production caller (ScoringEngine.cs, the ScoreResult.Create at the end
// of Score) — it landed in the same chain as this file, so the note that used to sit here saying
// nothing constructs one "yet" was stale by the time anyone read it. These tests still earn their
// place: the engine only ever hands Create a well-formed pair, so every guard below is reachable from
// the type's public surface and from nowhere the engine goes.
public class ScoreResultTests
{
    private static readonly ScoringWeightsSnapshot DefaultWeights = ScoringWeightsSnapshot.Default();

    [Fact]
    public void Create_rejects_a_null_breakdown()
    {
        var act = () => ScoreResult.Create(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_without_recommendations_yields_an_empty_list_not_null()
    {
        var result = ScoreResult.Create(Breakdown());

        result.Recommendations.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Create_defensively_copies_the_recommendations()
    {
        var source = new List<Recommendation> { Advice("Add Kubernetes.") };

        var result = ScoreResult.Create(Breakdown(), source);
        source.Add(Advice("Mention SQL."));

        result.Recommendations.Should().ContainSingle();
    }

    [Fact]
    public void Create_keeps_the_recommendations_it_was_given()
    {
        var advice = Advice("Add Kubernetes.");

        var result = ScoreResult.Create(Breakdown(), [advice]);

        result.Recommendations.Should().ContainSingle().Which.Should().Be(advice);
    }

    // The three pass-throughs. They exist so a caller never holds a stale copy of a derived value, so
    // what has to be true is that they AGREE with the breakdown rather than merely return something.
    [Fact]
    public void WeightedTotal_Sections_and_Weights_agree_with_the_breakdown()
    {
        var breakdown = Breakdown();

        var result = ScoreResult.Create(breakdown);

        result.WeightedTotal.Should().Be(breakdown.WeightedTotal);
        result.Sections.Should().Equal(breakdown.Sections);
        result.Weights.Should().Be(breakdown.Weights);
        result.Breakdown.Should().Be(breakdown);
    }

    // Not OverallScore: Analysis.OverallScore is an int 0..100 and this is a double 0..1. The names
    // are kept apart on purpose, and a rename back would be caught here.
    [Fact]
    public void WeightedTotal_is_on_the_zero_to_one_scale_not_the_percentage_scale()
    {
        var result = ScoreResult.Create(ScoreBreakdown.Create(1.0, 1.0, 1.0, 1.0, 1.0, 1.0, DefaultWeights));

        result.WeightedTotal.Should().BeApproximately(1.0, 0.0001);
    }

    private static ScoreBreakdown Breakdown() =>
        ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.5, 0.4, DefaultWeights);

    private static Recommendation Advice(string message) =>
        Recommendation.Create(
            SectionType.Skills, RecommendationPriority.Critical, RecommendationKind.MissingMustHaveSkill,
            message, 0.45);
}

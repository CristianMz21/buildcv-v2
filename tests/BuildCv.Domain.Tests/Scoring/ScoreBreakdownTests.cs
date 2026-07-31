using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

public class ScoreBreakdownTests
{
    private static readonly ScoringWeightsSnapshot DefaultWeights = new(
        Skills: 0.45, Experience: 0.20, Education: 0.20,
        Certifications: 0.10, Projects: 0.05);

    [Fact]
    public void WeightedTotal_calculates_correctly()
    {
        var breakdown = new ScoreBreakdown(
            MatchScore: 0.9,
            StructureScore: 0.8,
            AchievementsScore: 0.7,
            FormatScore: 0.6,
            LengthScore: 1.0,
            Weights: DefaultWeights);

        var total = breakdown.WeightedTotal;

        var expected = 0.45 * 0.9 + 0.20 * 0.8 + 0.20 * 0.7 + 0.10 * 0.6 + 0.05 * 1.0;
        total.Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void WeightedTotal_with_zero_scores()
    {
        var breakdown = new ScoreBreakdown(
            MatchScore: 0.0,
            StructureScore: 0.0,
            AchievementsScore: 0.0,
            FormatScore: 0.0,
            LengthScore: 0.0,
            Weights: DefaultWeights);

        breakdown.WeightedTotal.Should().Be(0.0);
    }

    [Fact]
    public void WeightedTotal_with_perfect_scores()
    {
        var breakdown = new ScoreBreakdown(
            MatchScore: 1.0,
            StructureScore: 1.0,
            AchievementsScore: 1.0,
            FormatScore: 1.0,
            LengthScore: 1.0,
            Weights: DefaultWeights);

        breakdown.WeightedTotal.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ScoreBreakdown_is_immutable()
    {
        var b1 = new ScoreBreakdown(0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights);
        var b2 = b1 with { MatchScore = 0.9 };

        b1.MatchScore.Should().Be(0.5);
        b2.MatchScore.Should().Be(0.9);
    }
}

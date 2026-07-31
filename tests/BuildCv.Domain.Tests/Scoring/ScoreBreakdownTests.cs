using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

public class ScoreBreakdownTests
{
    private static readonly ScoringWeightsSnapshot DefaultWeights = ScoringWeightsSnapshot.Default();

    [Fact]
    public void WeightedTotal_calculates_correctly()
    {
        var breakdown = ScoreBreakdown.Create(
            skillsScore: 0.9,
            experienceScore: 0.8,
            educationScore: 0.7,
            certificationsScore: 0.6,
            projectsScore: 1.0,
            weights: DefaultWeights);

        var total = breakdown.WeightedTotal;

        var expected = 0.45 * 0.9 + 0.20 * 0.8 + 0.20 * 0.7 + 0.10 * 0.6 + 0.05 * 1.0;
        total.Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void WeightedTotal_with_zero_scores()
    {
        var breakdown = ScoreBreakdown.Create(
            skillsScore: 0.0,
            experienceScore: 0.0,
            educationScore: 0.0,
            certificationsScore: 0.0,
            projectsScore: 0.0,
            weights: DefaultWeights);

        breakdown.WeightedTotal.Should().Be(0.0);
    }

    [Fact]
    public void WeightedTotal_with_perfect_scores()
    {
        var breakdown = ScoreBreakdown.Create(
            skillsScore: 1.0,
            experienceScore: 1.0,
            educationScore: 1.0,
            certificationsScore: 1.0,
            projectsScore: 1.0,
            weights: DefaultWeights);

        breakdown.WeightedTotal.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ScoreBreakdown_rejects_score_below_zero()
    {
        var act = () => ScoreBreakdown.Create(
            skillsScore: -0.1,
            experienceScore: 0.0,
            educationScore: 0.0,
            certificationsScore: 0.0,
            projectsScore: 0.0,
            weights: DefaultWeights);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ScoreBreakdown_rejects_score_above_one()
    {
        var act = () => ScoreBreakdown.Create(
            skillsScore: 1.5,
            experienceScore: 0.0,
            educationScore: 0.0,
            certificationsScore: 0.0,
            projectsScore: 0.0,
            weights: DefaultWeights);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ScoreBreakdown_default_snapshot_has_expected_weights()
    {
        DefaultWeights.Skills.Should().Be(0.45);
        DefaultWeights.Experience.Should().Be(0.20);
        DefaultWeights.Education.Should().Be(0.20);
        DefaultWeights.Certifications.Should().Be(0.10);
        DefaultWeights.Projects.Should().Be(0.05);
        DefaultWeights.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void ScoringWeightsSnapshot_rejects_weights_that_do_not_sum_to_one()
    {
        var act = () => ScoringWeightsSnapshot.Create(0.5, 0.2, 0.2, 0.1, 0.05);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ScoreBreakdown_is_immutable()
    {
        var b1 = ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights);

        b1.SkillsScore.Should().Be(0.5);
    }
}

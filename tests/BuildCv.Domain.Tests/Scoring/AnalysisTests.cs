using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

public class AnalysisTests
{
    private static readonly ScoringWeightsSnapshot DefaultWeights = new(
        Skills: 0.45, Experience: 0.20, Education: 0.20,
        Certifications: 0.10, Projects: 0.05);

    [Fact]
    public void Analysis_with_low_band_can_be_created()
    {
        var breakdown = new ScoreBreakdown(0.3, 0.4, 0.2, 0.5, 0.8, DefaultWeights);
        var analysis = new Analysis(
            Breakdown: breakdown,
            CandidateName: "Cristian Arellano",
            JobTitle: "Senior .NET Developer",
            ScoredAt: DateTimeOffset.Now)
        {
            Recommendations = ["Add more skills", "Improve summary"]
        };

        analysis.OverallScore.Should().Be(34);
        analysis.Band.Should().Be(ScoreBand.Low);
        analysis.Recommendations.Should().HaveCount(2);
    }

    [Fact]
    public void Analysis_with_defaults_can_be_created()
    {
        var breakdown = new ScoreBreakdown(0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights);
        var analysis = new Analysis(
            Breakdown: breakdown,
            CandidateName: "Cristian Arellano",
            JobTitle: "Senior .NET Developer",
            ScoredAt: DateTimeOffset.Now);

        analysis.Recommendations.Should().BeEmpty();
    }

    [Fact]
    public void Analysis_is_immutable()
    {
        var a1 = new Analysis(
            Breakdown: new ScoreBreakdown(0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights),
            CandidateName: "Cristian Arellano",
            JobTitle: "Senior .NET Developer",
            ScoredAt: DateTimeOffset.Now);

        var a2 = a1 with { CandidateName = "Jane Doe" };

        a1.CandidateName.Should().Be("Cristian Arellano");
        a2.CandidateName.Should().Be("Jane Doe");
    }
}

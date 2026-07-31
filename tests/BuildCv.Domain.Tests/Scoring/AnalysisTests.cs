using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

public class AnalysisTests
{
    [Fact]
    public void Analysis_with_bajo_band_can_be_created()
    {
        var breakdown = new ScoreBreakdown(0.3, 0.4, 0.2, 0.5, 0.8);
        var analysis = new Analysis(
            Breakdown: breakdown,
            OverallScore: 35,
            Band: ScoreBand.Bajo,
            Recommendations: ["Add more skills", "Improve summary"]);

        analysis.OverallScore.Should().Be(35);
        analysis.Band.Should().Be(ScoreBand.Bajo);
        analysis.Recommendations.Should().HaveCount(2);
    }

    [Fact]
    public void Analysis_is_immutable()
    {
        var a1 = new Analysis(
            Breakdown: new ScoreBreakdown(0.5, 0.5, 0.5, 0.5, 0.5),
            OverallScore: 50,
            Band: ScoreBand.Medio,
            Recommendations: []);

        var a2 = a1 with { OverallScore = 75, Band = ScoreBand.Bueno };

        a1.OverallScore.Should().Be(50);
        a2.OverallScore.Should().Be(75);
    }
}

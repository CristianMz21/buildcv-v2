using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

public class AnalysisTests
{
    private static readonly ScoringWeightsSnapshot DefaultWeights = ScoringWeightsSnapshot.Default();

    [Fact]
    public void Analysis_with_low_band_can_be_created()
    {
        var breakdown = ScoreBreakdown.Create(0.3, 0.4, 0.2, 0.5, 0.8, DefaultWeights);
        var analysis = new Analysis(
            Id: AnalysisId.New(),
            Breakdown: breakdown,
            ResumeId: ResumeId.New(),
            JobPostingId: JobPostingId.New(),
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
        var breakdown = ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights);
        var analysis = new Analysis(
            Id: AnalysisId.New(),
            Breakdown: breakdown,
            ResumeId: ResumeId.New(),
            JobPostingId: JobPostingId.New(),
            ScoredAt: DateTimeOffset.Now);

        analysis.Recommendations.Should().BeEmpty();
    }

    [Fact]
    public void Analysis_band_thresholds()
    {
        var medium = BuildAnalysis(ScoreBreakdown.Create(0.45, 0.45, 0.45, 0.45, 0.45, DefaultWeights));
        var good = BuildAnalysis(ScoreBreakdown.Create(0.65, 0.65, 0.65, 0.65, 0.65, DefaultWeights));
        var strong = BuildAnalysis(ScoreBreakdown.Create(0.9, 0.9, 0.9, 0.9, 0.9, DefaultWeights));

        medium.Band.Should().Be(ScoreBand.Medium);
        good.Band.Should().Be(ScoreBand.Good);
        strong.Band.Should().Be(ScoreBand.Strong);
    }

    private static Analysis BuildAnalysis(ScoreBreakdown breakdown) =>
        new(
            Id: AnalysisId.New(),
            Breakdown: breakdown,
            ResumeId: ResumeId.New(),
            JobPostingId: JobPostingId.New(),
            ScoredAt: DateTimeOffset.Now);

    [Fact]
    public void Analysis_is_immutable()
    {
        var a1 = new Analysis(
            Id: AnalysisId.New(),
            Breakdown: ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights),
            ResumeId: ResumeId.New(),
            JobPostingId: JobPostingId.New(),
            ScoredAt: DateTimeOffset.Now);

        var a2 = a1 with { Recommendations = ["Add more skills"] };

        a1.Recommendations.Should().BeEmpty();
        a2.Recommendations.Should().HaveCount(1);
    }
}

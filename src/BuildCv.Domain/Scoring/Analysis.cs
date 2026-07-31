namespace BuildCv.Domain.Scoring;

using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;

public sealed record Analysis(
    AnalysisId Id,
    ScoreBreakdown Breakdown,
    ResumeId ResumeId,
    JobPostingId JobPostingId,
    DateTimeOffset ScoredAt)
{
    public int OverallScore => (int)Math.Round(Breakdown.WeightedTotal * 100);
    public ScoreBand Band => OverallScore switch
    {
        < 40 => ScoreBand.Low,
        < 60 => ScoreBand.Medium,
        < 80 => ScoreBand.Good,
        _ => ScoreBand.Strong
    };

    public IReadOnlyList<string> Recommendations { get; init; } = [];
}

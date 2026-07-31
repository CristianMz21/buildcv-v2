namespace BuildCv.Domain.Scoring;

public sealed record Analysis(
    ScoreBreakdown Breakdown,
    string CandidateName,
    string JobTitle,
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

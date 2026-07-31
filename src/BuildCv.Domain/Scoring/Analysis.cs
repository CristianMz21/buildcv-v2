namespace BuildCv.Domain.Scoring;

public sealed record Analysis(
    ScoreBreakdown Breakdown,
    int OverallScore,
    ScoreBand Band,
    string CandidateName,
    string JobTitle)
{
    public IReadOnlyList<string> Recommendations { get; init; } = [];
}

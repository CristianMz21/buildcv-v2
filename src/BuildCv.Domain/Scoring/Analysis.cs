namespace BuildCv.Domain.Scoring;

public sealed record Analysis(
    ScoreBreakdown Breakdown,
    int OverallScore,
    ScoreBand Band,
    IReadOnlyList<string> Recommendations);

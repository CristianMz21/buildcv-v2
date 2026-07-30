namespace BuildCv.Domain.Scoring;

public class ScoreBreakdown
{
    public required double MatchScore { get; init; }
    public required double StructureScore { get; init; }
    public required double AchievementsScore { get; init; }
    public required double FormatScore { get; init; }
    public required double LengthScore { get; init; }

    public double WeightedTotal =>
        0.45 * MatchScore +
        0.20 * StructureScore +
        0.20 * AchievementsScore +
        0.10 * FormatScore +
        0.05 * LengthScore;
}

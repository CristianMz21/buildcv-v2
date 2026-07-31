namespace BuildCv.Domain.Scoring;

public sealed record ScoreBreakdown(
    double MatchScore,
    double StructureScore,
    double AchievementsScore,
    double FormatScore,
    double LengthScore)
{
    public double WeightedTotal =>
        0.45 * MatchScore +
        0.20 * StructureScore +
        0.20 * AchievementsScore +
        0.10 * FormatScore +
        0.05 * LengthScore;
}

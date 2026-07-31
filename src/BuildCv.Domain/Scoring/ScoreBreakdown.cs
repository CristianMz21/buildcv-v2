namespace BuildCv.Domain.Scoring;

public sealed record ScoreBreakdown(
    double MatchScore,
    double StructureScore,
    double AchievementsScore,
    double FormatScore,
    double LengthScore)
{
    public double WeightedTotal =>
        ScoringWeights.Skills * MatchScore +
        ScoringWeights.Experience * StructureScore +
        ScoringWeights.Education * AchievementsScore +
        ScoringWeights.Certifications * FormatScore +
        ScoringWeights.Projects * LengthScore;
}

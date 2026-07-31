namespace BuildCv.Domain.Scoring;

public sealed record ScoreBreakdown(
    double MatchScore,
    double StructureScore,
    double AchievementsScore,
    double FormatScore,
    double LengthScore,
    ScoringWeightsSnapshot Weights)
{
    public double WeightedTotal =>
        Weights.Skills * MatchScore +
        Weights.Experience * StructureScore +
        Weights.Education * AchievementsScore +
        Weights.Certifications * FormatScore +
        Weights.Projects * LengthScore;
}

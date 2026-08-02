namespace BuildCv.Domain.Scoring;

// One section's score together with the weight it was counted under.
//
// The weight travels with the score deliberately: "Skills 0.4" says nothing on its own, and a caller
// that had to look the weight up separately could pair a score with the weights of a different
// snapshot. This type is COMPUTED from a ScoreBreakdown and never persisted — the breakdown's own
// columns are the record.
//
// It deliberately carries no explanation text. That sentence is Recommendation.Message, which is
// encrypted because it quotes resume content; writing it twice and sealing only one copy would make
// the encryption theatre.
public sealed record SectionScore
{
    public SectionType Section { get; }
    public double Score { get; }
    public double Weight { get; }

    private SectionScore(SectionType section, double score, double weight)
    {
        Section = section;
        Score = score;
        Weight = weight;
    }

    public static SectionScore Create(SectionType section, double score, double weight)
    {
        ValidateUnitInterval(score, "Score", nameof(score));
        ValidateUnitInterval(weight, "Weight", nameof(weight));
        return new SectionScore(section, score, weight);
    }

    private static void ValidateUnitInterval(double value, string label, string paramName)
    {
        if (value < 0 || value > 1)
            throw new ArgumentException($"{label} must be between 0 and 1.", paramName);
    }
}

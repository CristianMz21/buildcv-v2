namespace BuildCv.Domain.Readability;

// One readability section's score together with the weight it was counted under.
//
// The weight travels with the score deliberately: "Chronology 0.4" says nothing on its own, and a caller
// that had to look the weight up separately could pair a score with the weights of a different snapshot.
// This type is COMPUTED from a ReadabilityBreakdown and never persisted -- the breakdown's own columns
// are the record.
//
// It deliberately carries no explanation text. That sentence is ReadabilityRecommendation.Message, which
// is encrypted because it quotes resume content; writing it twice and sealing only one copy would make
// the encryption theatre.
public sealed record ReadabilitySectionScore
{
    public ReadabilitySectionType Section { get; }
    public double Score { get; }
    public double Weight { get; }

    private ReadabilitySectionScore(ReadabilitySectionType section, double score, double weight)
    {
        Section = section;
        Score = score;
        Weight = weight;
    }

    public static ReadabilitySectionScore Create(ReadabilitySectionType section, double score, double weight)
    {
        ValidateUnitInterval(score, "Score", nameof(score));
        ValidateUnitInterval(weight, "Weight", nameof(weight));
        return new ReadabilitySectionScore(section, score, weight);
    }

    // FINITE first: every comparison below is false for NaN, so it would pass the range check
    // unchallenged. Weight is a renormalized share -- the output of a division -- which is what turns
    // that from theoretical into reachable.
    private static void ValidateUnitInterval(double value, string label, string paramName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException($"{label} must be a finite number.", paramName);
        if (value < 0 || value > 1)
            throw new ArgumentException($"{label} must be between 0 and 1.", paramName);
    }
}

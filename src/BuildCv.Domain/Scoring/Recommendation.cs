namespace BuildCv.Domain.Scoring;

using BuildCv.Domain.Exceptions;

// One piece of deterministic advice about the gap between a resume and a posting.
//
// The split between Kind and Message is the whole point of the type. Message is the sentence shown to
// the candidate; it quotes their resume and the posting, so it is sealed at rest. Kind names the RULE
// that produced it and stays plaintext, so "which advice do we give most often" survives the
// encryption instead of being traded away by it.
//
// Impact is how much of the total score acting on this would recover, on the same 0..1 scale as every
// score in this folder — it is what lets a client sort advice by what it is worth rather than by the
// order the rules happened to run in.
public sealed record Recommendation
{
    public SectionType Section { get; }
    public RecommendationPriority Priority { get; }
    public RecommendationKind Kind { get; }
    public string Message { get; }
    public double Impact { get; }

    private Recommendation(
        SectionType section,
        RecommendationPriority priority,
        RecommendationKind kind,
        string message,
        double impact)
    {
        Section = section;
        Priority = priority;
        Kind = kind;
        Message = message;
        Impact = impact;
    }

    public static Recommendation Create(
        SectionType section,
        RecommendationPriority priority,
        RecommendationKind kind,
        string message,
        double impact)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidRecommendationException("Recommendation message is required.");

        // FINITE first. Both comparisons below are false for NaN, so a NaN impact would sail through the
        // range check and be persisted as advice worth an unknowable amount — and it would sort
        // arbitrarily against every other recommendation. Not a theoretical case any more: Impact is a
        // section weight times a score delta, and section weights are now produced by a division.
        if (!double.IsFinite(impact))
            throw new InvalidRecommendationException($"Recommendation impact must be a finite number (actual: {impact}).");
        if (impact < 0 || impact > 1)
            throw new InvalidRecommendationException($"Recommendation impact must be between 0 and 1 (actual: {impact}).");

        return new Recommendation(section, priority, kind, message.Trim(), impact);
    }
}

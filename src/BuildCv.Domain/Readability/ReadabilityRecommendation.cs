namespace BuildCv.Domain.Readability;

using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Scoring;

// One piece of deterministic advice about the resume ITSELF -- no posting involved, which is what makes
// it the half of the product a candidate can use before they have a target job.
//
// The split between Kind and Message is the whole point of the type, and it is copied from
// Scoring.Recommendation verbatim because the argument is identical. Message is the sentence shown to
// the candidate; it quotes their resume, so it is sealed at rest. Kind names the RULE that produced it
// and stays plaintext, so "which advice do we give most often" survives the encryption instead of being
// traded away by it.
//
// Impact is how much of the readability total acting on this would recover, on the same 0..1 scale as
// every score in this folder -- it is what lets a client sort advice by what it is worth rather than by
// the order the rules happened to run in.
//
// PRIORITY IS Scoring.RecommendationPriority, AND THAT REUSE IS DELIBERATE while
// ReadabilitySectionType and ReadabilityBand are not. Priority grades ADVICE, not a score: both engines
// derive it from the same 0..1 Impact scale through the same two thresholds, and a candidate reads one
// to-do list. Nothing projects Enum.GetValues over it, so it carries none of the closed-at-six problem
// SectionType has. A band, by contrast, grades a SCORE, and the two scores answer questions about
// different subjects -- one name over both is how a client ends up blending them.
public sealed record ReadabilityRecommendation
{
    public ReadabilitySectionType Section { get; }
    public RecommendationPriority Priority { get; }
    public ReadabilityRecommendationKind Kind { get; }
    public string Message { get; }
    public double Impact { get; }

    private ReadabilityRecommendation(
        ReadabilitySectionType section,
        RecommendationPriority priority,
        ReadabilityRecommendationKind kind,
        string message,
        double impact)
    {
        Section = section;
        Priority = priority;
        Kind = kind;
        Message = message;
        Impact = impact;
    }

    public static ReadabilityRecommendation Create(
        ReadabilitySectionType section,
        RecommendationPriority priority,
        ReadabilityRecommendationKind kind,
        string message,
        double impact)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidRecommendationException("Recommendation message is required.");

        // FINITE first. Both comparisons below are false for NaN, so a NaN impact would sail through the
        // range check and be persisted as advice worth an unknowable amount -- and it would sort
        // arbitrarily against every other recommendation. Reachable: Impact is a section weight times a
        // score delta, and section weights are produced by a division.
        if (!double.IsFinite(impact))
            throw new InvalidRecommendationException($"Recommendation impact must be a finite number (actual: {impact}).");
        if (impact < 0 || impact > 1)
            throw new InvalidRecommendationException($"Recommendation impact must be between 0 and 1 (actual: {impact}).");

        return new ReadabilityRecommendation(section, priority, kind, message.Trim(), impact);
    }
}

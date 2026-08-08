namespace BuildCv.Domain.Readability;

using BuildCv.Domain.Resumes;

// HOW READABLE ONE RESUME IS, taken at one moment. A structural sibling of Scoring.Analysis and
// deliberately NOT a part of it.
//
// The subject is the difference, and it is not a matter of taste. An Analysis is a fact about a
// (resume, posting) PAIR: it requires a non-nullable JobPostingId, and its authorization story, its
// de-duplication key and its staleness predicate are all written in terms of two aggregates. A
// ReadabilityReport is a fact about a RESUME, and the whole point of it is that a candidate gets one
// before they have a posting at all. Making JobPostingId nullable to fit this in would leave one
// append-only fact table meaning two different things, with no column able to say which.
//
// The other two blockers are in the types beside this one: ReadabilitySectionType explains why the
// scoring enum could not simply grow five members, and ReadabilityWeightsSnapshot explains why the two
// weightings cannot be one. THE TWO TOTALS ARE NEVER BLENDED SERVER-SIDE -- they answer different
// questions about different subjects, and a combined figure would be explainable as neither. A combined
// display is the client's business.
public sealed class ReadabilityReport
{
    // A real List behind an IReadOnlyList, the same shape every other owned collection in the model
    // uses: EF has to be able to ADD to it while materializing, and the getter hands callers a wrapper
    // they cannot mutate.
    private readonly List<ReadabilityRecommendation> _recommendations = [];

    public ReadabilityReportId Id { get; }
    public ReadabilityBreakdown Breakdown { get; }
    public ResumeId ResumeId { get; }
    public DateTimeOffset EvaluatedAt { get; }

    // UNORDERED. This is a set, not a sequence: the child table carries a surrogate key and no stored
    // position, so a reloaded report hands these back in whatever order the server chose -- which is NOT
    // the order they were added in, and is not stable between reads.
    //
    // Anything presenting these to a candidate has to sort them explicitly through
    // ReadabilityRecommendationOrder: Priority ASCENDING, then Impact DESCENDING.
    public IReadOnlyList<ReadabilityRecommendation> Recommendations => _recommendations.AsReadOnly();

    // NEVER OverallScore. That name means "match against this posting" on Analysis and must keep meaning
    // only that: the two numbers are on the same 0..100 scale and measure different things, so one name
    // over both is how a client ends up charting them against each other.
    public int ReadabilityScore => (int)Math.Round(Breakdown.WeightedTotal * 100);

    public ReadabilityBand Band => ReadabilityScore switch
    {
        < 40 => ReadabilityBand.Low,
        < 60 => ReadabilityBand.Medium,
        < 80 => ReadabilityBand.Good,
        _ => ReadabilityBand.Strong
    };

    private ReadabilityReport(
        ReadabilityReportId id,
        ReadabilityBreakdown breakdown,
        ResumeId resumeId,
        DateTimeOffset evaluatedAt,
        IReadOnlyList<ReadabilityRecommendation> recommendations)
    {
        Id = id;
        Breakdown = breakdown;
        ResumeId = resumeId;
        EvaluatedAt = evaluatedAt;
        _recommendations = [.. recommendations];
    }

#pragma warning disable CS8618 // EF Core assigns every mapped member immediately after construction.
    private ReadabilityReport() { }
#pragma warning restore CS8618

    public static ReadabilityReport Create(
        ReadabilityReportId id,
        ReadabilityBreakdown breakdown,
        ResumeId resumeId,
        DateTimeOffset evaluatedAt,
        IReadOnlyList<ReadabilityRecommendation>? recommendations = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(breakdown);
        ArgumentNullException.ThrowIfNull(resumeId);
        return new ReadabilityReport(id, breakdown, resumeId, evaluatedAt, recommendations ?? []);
    }

    public override bool Equals(object? obj) => obj is ReadabilityReport other && Id.Equals(other.Id);
    public override int GetHashCode() => Id.GetHashCode();
}

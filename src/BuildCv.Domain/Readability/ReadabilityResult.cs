namespace BuildCv.Domain.Readability;

// What one readability run produced: the numbers, and the advice derived from the same pass.
//
// It stores exactly two members. WeightedTotal, Sections and Weights are pass-throughs to Breakdown
// rather than stored copies -- a second copy of a derived value is a second source of truth, and this
// codebase Ignores even one computed double for that reason.
//
// The total is called WeightedTotal, NOT ReadabilityScore. ReadabilityReport.ReadabilityScore is an int
// 0..100 and this is a double 0..1: the same name on two scales, one call site apart, is a bug waiting
// to be written.
//
// It is never persisted and never an entity. ReadabilityReport is what gets stored.
public sealed record ReadabilityResult
{
    public ReadabilityBreakdown Breakdown { get; }
    public IReadOnlyList<ReadabilityRecommendation> Recommendations { get; }

    private ReadabilityResult(
        ReadabilityBreakdown breakdown, IReadOnlyList<ReadabilityRecommendation> recommendations)
    {
        Breakdown = breakdown;
        Recommendations = recommendations;
    }

    public static ReadabilityResult Create(
        ReadabilityBreakdown breakdown, IReadOnlyList<ReadabilityRecommendation>? recommendations = null)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        return new ReadabilityResult(breakdown, recommendations is null ? [] : [.. recommendations]);
    }

    public double WeightedTotal => Breakdown.WeightedTotal;
    public IReadOnlyList<ReadabilitySectionScore> Sections => Breakdown.Sections;
    public ReadabilityWeightsSnapshot Weights => Breakdown.Weights;
}

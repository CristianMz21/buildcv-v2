namespace BuildCv.Domain.Scoring;

// What one scoring run produced: the numbers, and the advice derived from the same pass.
//
// It stores exactly two members. WeightedTotal, Sections and Weights are pass-throughs to Breakdown
// rather than stored copies — a second copy of a derived value is a second source of truth, and this
// codebase Ignores even one computed double for that reason.
//
// The total is called WeightedTotal, NOT OverallScore. Analysis.OverallScore is an int 0..100 and this
// is a double 0..1: the same name on two scales, one call site apart, is a bug waiting to be written.
//
// It is never persisted and never an entity. Analysis is what gets stored.
public sealed record ScoreResult
{
    public ScoreBreakdown Breakdown { get; }
    public IReadOnlyList<Recommendation> Recommendations { get; }

    private ScoreResult(ScoreBreakdown breakdown, IReadOnlyList<Recommendation> recommendations)
    {
        Breakdown = breakdown;
        Recommendations = recommendations;
    }

    public static ScoreResult Create(ScoreBreakdown breakdown, IReadOnlyList<Recommendation>? recommendations = null)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        return new ScoreResult(breakdown, recommendations is null ? [] : [.. recommendations]);
    }

    public double WeightedTotal => Breakdown.WeightedTotal;
    public IReadOnlyList<SectionScore> Sections => Breakdown.Sections;
    public ScoringWeightsSnapshot Weights => Breakdown.Weights;
}

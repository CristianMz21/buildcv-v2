namespace BuildCv.Domain.Scoring;

using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;

public sealed class Analysis
{
    // A real List behind an IReadOnlyList, the same shape every other owned collection in the model
    // uses: EF has to be able to ADD to it while materializing, and the getter hands callers a wrapper
    // they cannot mutate.
    private readonly List<Recommendation> _recommendations = [];

    public AnalysisId Id { get; }
    public ScoreBreakdown Breakdown { get; }
    public ResumeId ResumeId { get; }
    public JobPostingId JobPostingId { get; }
    public DateTimeOffset ScoredAt { get; }

    // UNORDERED. This is a set, not a sequence: the child table carries a surrogate key and no stored
    // position, so a reloaded analysis hands these back in whatever order the server chose — which is
    // NOT the order they were added in, and is not stable between reads.
    //
    // Anything presenting these to a candidate has to sort them explicitly: Priority ASCENDING, then
    // Impact DESCENDING. The directions differ and both are easy to get backwards — Critical is 0, so
    // ascending Priority puts the urgent advice first, while Impact is "how much score this recovers",
    // so the biggest wins come first within a priority.
    //
    // Insertion order is the assumption to avoid; the reason there is no position column is that a
    // stored one is a lie the moment a rule is added or removed, the same argument ChildTable makes
    // about positional keys.
    public IReadOnlyList<Recommendation> Recommendations => _recommendations.AsReadOnly();

    public int OverallScore => (int)Math.Round(Breakdown.WeightedTotal * 100);
    public ScoreBand Band => OverallScore switch
    {
        < 40 => ScoreBand.Low,
        < 60 => ScoreBand.Medium,
        < 80 => ScoreBand.Good,
        _ => ScoreBand.Strong
    };

    private Analysis(
        AnalysisId id,
        ScoreBreakdown breakdown,
        ResumeId resumeId,
        JobPostingId jobPostingId,
        DateTimeOffset scoredAt,
        IReadOnlyList<Recommendation> recommendations)
    {
        Id = id;
        Breakdown = breakdown;
        ResumeId = resumeId;
        JobPostingId = jobPostingId;
        ScoredAt = scoredAt;
        _recommendations = [.. recommendations];
    }

#pragma warning disable CS8618 // EF Core assigns every mapped member immediately after construction.
    private Analysis() { }
#pragma warning restore CS8618

    public static Analysis Create(
        AnalysisId id,
        ScoreBreakdown breakdown,
        ResumeId resumeId,
        JobPostingId jobPostingId,
        DateTimeOffset scoredAt,
        IReadOnlyList<Recommendation>? recommendations = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(breakdown);
        ArgumentNullException.ThrowIfNull(resumeId);
        ArgumentNullException.ThrowIfNull(jobPostingId);
        return new Analysis(id, breakdown, resumeId, jobPostingId, scoredAt, recommendations ?? []);
    }

    public override bool Equals(object? obj) => obj is Analysis other && Id.Equals(other.Id);
    public override int GetHashCode() => Id.GetHashCode();
}

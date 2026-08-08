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

    // WHICH VERSIONS this score was taken against. ResumeId and JobPostingId name the aggregates; these
    // name the state they were in, which is the whole difference between "a score of my CV" and "a score
    // of the CV I had on Tuesday".
    //
    // UpdatedAt, NOT the rowversion beside it, and not a content hash. Both aggregates carry a SQL Server
    // rowversion, but only as a shadow property with no Domain member — and it bumps on a child-collection
    // change only BECAUSE Touch() writes UpdatedAt to the root row, so the two have identical sensitivity
    // and the identical failure mode: a mutator that forgets Touch(). Using the member already in Domain
    // costs no shadow-property extraction, no port change and no EF concept in here. A content hash would
    // need a canonical serialization of a ten-collection aggregate INCLUDING its encrypted PII — an
    // equality oracle over the very fields this repo seals.
    //
    // NULLABLE, and null does NOT mean "unchanged": it means UNKNOWN. Rows written before these columns
    // existed cannot say what they scored. Every predicate that reads them treats unknown as STALE rather
    // than fresh, which is the direction whose cost is one wasted re-score instead of a candidate told a
    // number is current when it is not.
    public DateTimeOffset? ResumeUpdatedAt { get; }
    public DateTimeOffset? JobPostingUpdatedAt { get; }

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
        IReadOnlyList<Recommendation> recommendations,
        DateTimeOffset? resumeUpdatedAt,
        DateTimeOffset? jobPostingUpdatedAt)
    {
        Id = id;
        Breakdown = breakdown;
        ResumeId = resumeId;
        JobPostingId = jobPostingId;
        ScoredAt = scoredAt;
        _recommendations = [.. recommendations];
        ResumeUpdatedAt = resumeUpdatedAt;
        JobPostingUpdatedAt = jobPostingUpdatedAt;
    }

#pragma warning disable CS8618 // EF Core assigns every mapped member immediately after construction.
    private Analysis() { }
#pragma warning restore CS8618

    // The two provenance arguments DEFAULT TO NULL, which is the honest value for a caller that does not
    // know what it scored — a test double reconstructing a row, or any writer that never saw the source
    // aggregates. It is also the fail-safe one: a forgotten argument makes the analysis read as stale
    // forever and be re-scored every time, never the reverse. The one production writer is
    // ScoreResumeHandler, and it passes both from the aggregates it already loaded.
    public static Analysis Create(
        AnalysisId id,
        ScoreBreakdown breakdown,
        ResumeId resumeId,
        JobPostingId jobPostingId,
        DateTimeOffset scoredAt,
        IReadOnlyList<Recommendation>? recommendations = null,
        DateTimeOffset? resumeUpdatedAt = null,
        DateTimeOffset? jobPostingUpdatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(breakdown);
        ArgumentNullException.ThrowIfNull(resumeId);
        ArgumentNullException.ThrowIfNull(jobPostingId);
        return new Analysis(
            id, breakdown, resumeId, jobPostingId, scoredAt, recommendations ?? [],
            resumeUpdatedAt, jobPostingUpdatedAt);
    }

    // Does this score still describe the resume as it stands now?
    //
    // EQUALITY, never `>`. The obvious "the resume was touched after I scored it" reads as
    // `resumeUpdatedAt > ResumeUpdatedAt`, and it is wrong in a way that is invisible until it matters:
    // UpdatedAt is written by whichever process handled the mutation, so two writers with skewed clocks
    // can leave a resume whose UpdatedAt is EARLIER than the one recorded here, and `>` calls that fresh.
    // Equality has no direction to get backwards — anything other than the exact instant recorded is a
    // different version of the resume.
    //
    // Unknown provenance (null) is therefore stale, which is the fail-safe answer rather than a special
    // case: no instant equals null.
    public bool IsStaleFor(DateTimeOffset resumeUpdatedAt) => ResumeUpdatedAt != resumeUpdatedAt;

    public override bool Equals(object? obj) => obj is Analysis other && Id.Equals(other.Id);
    public override int GetHashCode() => Id.GetHashCode();
}

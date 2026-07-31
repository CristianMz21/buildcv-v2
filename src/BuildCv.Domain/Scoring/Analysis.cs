namespace BuildCv.Domain.Scoring;

using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;

public sealed class Analysis
{
    public AnalysisId Id { get; }
    public ScoreBreakdown Breakdown { get; }
    public ResumeId ResumeId { get; }
    public JobPostingId JobPostingId { get; }
    public DateTimeOffset ScoredAt { get; }
    public IReadOnlyList<string> Recommendations { get; }

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
        IReadOnlyList<string> recommendations)
    {
        Id = id;
        Breakdown = breakdown;
        ResumeId = resumeId;
        JobPostingId = jobPostingId;
        ScoredAt = scoredAt;
        Recommendations = recommendations;
    }

    public static Analysis Create(
        AnalysisId id,
        ScoreBreakdown breakdown,
        ResumeId resumeId,
        JobPostingId jobPostingId,
        DateTimeOffset scoredAt,
        IReadOnlyList<string>? recommendations = null)
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

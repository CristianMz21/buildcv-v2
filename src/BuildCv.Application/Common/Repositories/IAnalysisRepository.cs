namespace BuildCv.Application.Common.Repositories;

using BuildCv.Application.Common.Pagination;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

public interface IAnalysisRepository
{
    Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default);

    // One analysis by its own id, with no owner filter — because an Analysis HAS no owner. It names a
    // resume, and the resume's owner is the owner, so authorization is a second read in the handler
    // rather than a parameter here. Denormalizing an AccountId onto an append-only fact to save that
    // read would give the platform two answers to "whose is this" and no way to tell which is stale.
    Task<Analysis?> GetByIdAsync(AnalysisId id, CancellationToken cancellationToken = default);

    // The most recent score for one (resume, posting) pair, or null if it has never been scored.
    //
    // ONE ROW, not a list, and it exists for exactly one caller: ScoreResumeHandler asks whether the score
    // it is about to compute already exists. Everything it needs to answer that is on the row — the two
    // provenance timestamps, the weights' SchemaVersion and ScoredAt — so this is a single read that
    // replaces a duplicate write, never a general query.
    //
    // "Most recent" is by INSERTION, the same axis GetPageByResumeIdAsync walks and for the same reason:
    // ScoredAt is supplied by the caller and two analyses of one pair can carry the same instant, so
    // ordering on it would be free to answer either way round. Only the latest row is consulted — an
    // older row that happens to match again (a resume edited and edited back) is deliberately not reused,
    // because the history is a sequence of scoring EVENTS and reaching backwards past one would reorder it.
    //
    // NOT a cache lookup. It reads the same table every other method here reads, through the same soft
    // -delete filter, so it cannot disagree with what a later read of the history will show — which is
    // exactly what an in-process cache beside this port could do.
    Task<Analysis?> GetLatestByPairAsync(
        ResumeId resumeId, JobPostingId jobPostingId, CancellationToken cancellationToken = default);

    // OLDEST first, unlike every other paged list here. A score history is read forwards — the first
    // run, then what changed — so the cursor walks the (ResumeId, Seq) index in its own direction and
    // the boundary comparison flips with it.
    Task<Page<Analysis>> GetPageByResumeIdAsync(ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default);
}

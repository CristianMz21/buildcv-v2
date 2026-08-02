namespace BuildCv.Application.Common.Repositories;

using BuildCv.Application.Common.Pagination;
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

    // OLDEST first, unlike every other paged list here. A score history is read forwards — the first
    // run, then what changed — so the cursor walks the (ResumeId, Seq) index in its own direction and
    // the boundary comparison flips with it.
    Task<Page<Analysis>> GetPageByResumeIdAsync(ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default);
}

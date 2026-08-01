namespace BuildCv.Application.Common.Repositories;

using BuildCv.Application.Common.Pagination;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

public interface IAnalysisRepository
{
    Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default);

    // OLDEST first, unlike every other paged list here. A score history is read forwards — the first
    // run, then what changed — so the cursor walks the (ResumeId, Seq) index in its own direction and
    // the boundary comparison flips with it.
    Task<Page<Analysis>> GetPageByResumeIdAsync(ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default);
}

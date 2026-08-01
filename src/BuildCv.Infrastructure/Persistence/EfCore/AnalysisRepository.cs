using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Analyses, against SQL Server. The port is append-and-read: a score is a fact about a moment, never
// edited afterwards, so there is no UpdateAsync to write.
//
// The ScoreBreakdown is an owned reference table-split into this row, so it loads with the analysis.
internal sealed class AnalysisRepository : IAnalysisRepository
{
    private readonly BuildCvDbContext _context;

    public AnalysisRepository(BuildCvDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        _context.Analyses.Add(analysis);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    // Score history for one resume, oldest first, walking the (ResumeId, Seq) index in its own order.
    // ScoredAt would read more naturally and order worse: it is supplied by the caller and two analyses
    // of the same resume can carry the same instant.
    //
    // Tombstoned analyses are excluded by the global query filter, which is what makes the cascade in
    // ResumeRepository.DeleteAsync observable through this port.
    public Task<Page<Analysis>> GetPageByResumeIdAsync(
        ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resumeId);
        ArgumentNullException.ThrowIfNull(page);

        return _context.Analyses
            .Where(analysis => analysis.ResumeId == resumeId)
            .ToOldestFirstPageAsync(page, cancellationToken);
    }
}

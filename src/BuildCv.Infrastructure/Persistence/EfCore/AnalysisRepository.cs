using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using Microsoft.EntityFrameworkCore;

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

    // One analysis, by its own key.
    //
    // NO AsSplitQuery, unlike ResumeRepository.GetByIdAsync, and the difference is the collection count
    // rather than a preference. Split query exists here to stop a CARTESIAN PRODUCT between two or more
    // owned collections joined onto the same principal; Analysis owns exactly one (Recommendations —
    // Breakdown is an owned REFERENCE table-split into the Analyses row, so it costs no join at all).
    // One collection is one LEFT JOIN, and the rows shipped are its count, not a product of anything.
    // Splitting it would buy a second round trip and a page that is no longer one atomic read.
    //
    // Tombstoned analyses come back as null through the global query filter — the same filter that makes
    // ResumeRepository.DeleteAsync's cascade observable, so "delete my resume" also hides every score
    // derived from it. Nothing in this method says so, which is the point of a filter on the model.
    public Task<Analysis?> GetByIdAsync(AnalysisId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _context.Analyses.FirstOrDefaultAsync(analysis => analysis.Id == id, cancellationToken);
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

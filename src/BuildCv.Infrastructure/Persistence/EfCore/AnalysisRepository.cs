using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Persistence.Conventions;
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
    // Splitting it would buy a second round trip and cost this read its atomicity — one statement for
    // the analysis and another for its recommendations, with a write free to land between them. On the
    // paged path that trade is worth making because the fan-out there is unbounded; for a single row
    // whose one collection is capped at ten there is nothing to buy.
    //
    // Tombstoned analyses come back as null through the global query filter — the same filter that makes
    // ResumeRepository.DeleteAsync's cascade observable, so "delete my resume" also hides every score
    // derived from it. Nothing in this method says so, which is the point of a filter on the model.
    public Task<Analysis?> GetByIdAsync(AnalysisId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _context.Analyses.FirstOrDefaultAsync(analysis => analysis.Id == id, cancellationToken);
    }

    // The newest score for one pair, which is the row ScoreResumeHandler compares against before writing
    // a duplicate.
    //
    // Ordered on the Seq shadow column rather than on ScoredAt — the same axis the history walks, and for
    // the same reason: ScoredAt is caller-supplied and two rows can share it, so it cannot break a tie.
    //
    // Served by the (ResumeId, Seq) index: SQL Server seeks the resume, walks Seq backwards and applies
    // JobPostingId as a residual predicate, so the first matching row ends the scan. A dedicated
    // (ResumeId, JobPostingId, Seq) index would turn that into a pure seek and is deliberately NOT added
    // — one resume's score history is a handful of rows, so the index would cost a write on every insert
    // to save a few page reads. Revisit if a resume is ever scored against hundreds of postings.
    //
    // NO AsSplitQuery, for the reason spelled out on GetByIdAsync: Analysis owns one collection, so there
    // is no cartesian product for a split to prevent.
    public Task<Analysis?> GetLatestByPairAsync(
        ResumeId resumeId, JobPostingId jobPostingId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resumeId);
        ArgumentNullException.ThrowIfNull(jobPostingId);

        return _context.Analyses
            .Where(analysis => analysis.ResumeId == resumeId && analysis.JobPostingId == jobPostingId)
            .OrderByDescending(analysis => EF.Property<long>(analysis, ShadowColumns.Seq))
            .FirstOrDefaultAsync(cancellationToken);
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

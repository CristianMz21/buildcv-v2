using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Readability reports, against SQL Server. The port is append-and-read: a report is a fact about a
// moment and is never edited afterwards, so there is no UpdateAsync to write.
//
// The ReadabilityBreakdown is an owned reference table-split into this row, so it loads with the report.
//
// Tombstoned reports disappear through the global query filter, which is the same filter that makes
// ResumeRepository.DeleteAsync's cascade observable — "delete my resume" hides every report derived from
// it. Nothing in this class says so, which is the point of a filter on the model.
internal sealed class ReadabilityReportRepository : IReadabilityReportRepository
{
    private readonly BuildCvDbContext _context;

    public ReadabilityReportRepository(BuildCvDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task AddAsync(ReadabilityReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        _context.ReadabilityReports.Add(report);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    // One report, by its own key.
    //
    // NO AsSplitQuery, matching AnalysisRepository.GetByIdAsync and for the reason written there rather
    // than by imitation: split query exists to stop a CARTESIAN PRODUCT between two or more owned
    // collections joined onto the same principal, and ReadabilityReport owns exactly one
    // (Recommendations — Breakdown is an owned REFERENCE table-split into the Reports row, so it costs no
    // join at all). One collection is one LEFT JOIN and the rows shipped are its count, not a product of
    // anything. Splitting would buy a second round trip and cost this read its atomicity. The paged path
    // below is the opposite trade, because there the fan-out is per principal and unbounded.
    public Task<ReadabilityReport?> GetByIdAsync(
        ReadabilityReportId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _context.ReadabilityReports.FirstOrDefaultAsync(report => report.Id == id, cancellationToken);
    }

    // Readability history for one resume, oldest first, walking the (ResumeId, Seq) index in its own
    // order — the index ReadabilityReportConfiguration created with the table for exactly this read.
    //
    // Ordered on Seq rather than on EvaluatedAt, matching AnalysisRepository: EvaluatedAt comes from the
    // handler's clock and two reports of one resume can carry the same instant, so it cannot break a tie.
    //
    // AsSplitQuery is not written here because it belongs to the shared probe in KeysetQueryExtensions,
    // which is where it has to be: a per-repository copy is what left score history unsplit until
    // Analysis grew a collection. ReadabilityReport already owns one, so a page of twenty reports each
    // carrying advice is exactly the fan-out that probe caps — KeysetQueryTranslationTests reads the
    // join count off the generated SQL, because every page-shape assertion passes either way.
    public Task<Page<ReadabilityReport>> GetPageByResumeIdAsync(
        ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resumeId);
        ArgumentNullException.ThrowIfNull(page);

        return _context.ReadabilityReports
            .Where(report => report.ResumeId == resumeId)
            .ToOldestFirstPageAsync(page, cancellationToken);
    }
}

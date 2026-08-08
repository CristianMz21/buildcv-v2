using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Readability;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Readability reports, against SQL Server. The port is append-only, so this is the whole adapter: a
// report is a fact about a moment and is never edited afterwards.
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
}

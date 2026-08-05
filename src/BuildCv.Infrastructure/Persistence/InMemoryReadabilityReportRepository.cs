using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Readability;

namespace BuildCv.Infrastructure.Persistence;

// The development and Api-test counterpart of ReadabilityReportRepository.
//
// It keeps the rows rather than discarding them, even though nothing reads them back through this port
// yet. A store whose Add did nothing would make every Api test pass against a write path that does not
// write — and this is the store the whole Api suite runs on, so a divergence here certifies behaviour
// production does not have.
public sealed class InMemoryReadabilityReportRepository : IReadabilityReportRepository
{
    private readonly ConcurrentDictionary<Guid, ReadabilityReport> _reports = new();

    // Exposed for the Api tests, which have no port method to read a report back with. It is a count and
    // not a getter: what those tests need to know is that a request WROTE, which "the response looks
    // right" cannot tell them either way.
    public int Count => _reports.Count;

    public Task AddAsync(ReadabilityReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        _reports[report.Id.Value] = report;
        return Task.CompletedTask;
    }
}

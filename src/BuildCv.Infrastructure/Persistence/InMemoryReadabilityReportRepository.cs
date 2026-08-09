using System.Collections.Concurrent;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;

namespace BuildCv.Infrastructure.Persistence;

// The development and Api-test counterpart of ReadabilityReportRepository.
//
// OLDEST first, matching it: a readability history is read forwards, for the reasons written on
// IReadabilityReportRepository.GetPageByResumeIdAsync. See InMemoryResumeRepository for why the
// insertion counter exists — it stands in for the bigint IDENTITY Seq column, and the Api suite runs
// entirely on this store, so a store that answered the list port in dictionary order would certify page
// behaviour SQL Server does not produce.
public sealed class InMemoryReadabilityReportRepository : IReadabilityReportRepository
{
    private readonly ConcurrentDictionary<Guid, KeysetRow<ReadabilityReport>> _reports = new();
    private long _sequence;

    // Exposed for the Api tests. It PREDATES the read methods below and is kept rather than replaced:
    // what those tests need to know is that a request WROTE, and a count across every resume in the host
    // says that without arranging an owner, a resume id or a page request to ask it with.
    public int Count => _reports.Count;

    public Task AddAsync(ReadabilityReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        _reports[report.Id.Value] = new KeysetRow<ReadabilityReport>(report, Interlocked.Increment(ref _sequence));
        return Task.CompletedTask;
    }

    // NO IsLive filter, for the reason spelled out on InMemoryAnalysisRepository.GetByIdAsync: a report
    // is an append-only fact with no Delete() and no Status, so there is no state a filter could read.
    // The cascade below removes the row instead, which is what the EF side amounts to once its query
    // filter has run.
    public Task<ReadabilityReport?> GetByIdAsync(
        ReadabilityReportId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _reports.TryGetValue(id.Value, out var row);
        return Task.FromResult(row?.Item);
    }

    public Task<Page<ReadabilityReport>> GetPageByResumeIdAsync(
        ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_reports.Values
            .Where(row => row.Item.ResumeId.Value == resumeId.Value)
            .ToOldestFirstPage(page));
    }

    // NOT ON IReadabilityReportRepository, and it must not go there — the same ruling
    // InMemoryAnalysisRepository.RemoveAllDerivedFrom carries, and the same reasons. The port describes
    // what a use case may ask of a readability history, and no use case deletes one; this is the
    // in-memory counterpart of ResumeRepository.CascadeToReadabilityReportsAsync, a repository-internal
    // cross-aggregate write with no handler behind it.
    //
    // Called only from InMemoryResumeRepository.DeleteAsync, which is why that store takes this one as a
    // constructor dependency rather than resolving IReadabilityReportRepository.
    internal void RemoveAllDerivedFrom(ResumeId resumeId)
    {
        foreach (var row in _reports.Values.Where(row => row.Item.ResumeId.Value == resumeId.Value))
            _reports.TryRemove(row.Item.Id.Value, out _);
    }
}

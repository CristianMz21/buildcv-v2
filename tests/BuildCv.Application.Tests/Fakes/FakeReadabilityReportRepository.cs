namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;

// Oldest first, matching ReadabilityReportRepository and InMemoryReadabilityReportRepository. See
// FakeResumeRepository for the counter.
public sealed class FakeReadabilityReportRepository : IReadabilityReportRepository
{
    private readonly List<KeysetRow<ReadabilityReport>> _reports = [];
    private long _sequence;

    public IReadOnlyList<ReadabilityReport> Reports => [.. _reports.Select(row => row.Item)];

    // Counts the inserts. "The result says success" and "a row was written" are different claims, and
    // only this one is about the store — a handler that returned a report it never persisted would pass
    // every assertion about the response.
    public int WriteCount { get; private set; }

    // Counts the calls that would have been a database round trip, for the same reason
    // FakeAnalysisRepository.ReadCount exists and with the same caveat: GetReadabilityHistoryHandler
    // authorizes by READING THE RESUME, so the resume counter moves on the forbidden and
    // malformed-cursor paths and can say nothing about ordering. The store that must stay untouched
    // when either is refused is this one, so the counter has to live here too.
    public int ReadCount { get; private set; }

    public Task AddAsync(ReadabilityReport report, CancellationToken cancellationToken = default)
    {
        WriteCount++;
        _reports.Add(new KeysetRow<ReadabilityReport>(report, ++_sequence));
        return Task.CompletedTask;
    }

    public Task<ReadabilityReport?> GetByIdAsync(
        ReadabilityReportId id, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_reports.FirstOrDefault(row => row.Item.Id == id)?.Item);
    }

    public Task<Page<ReadabilityReport>> GetPageByResumeIdAsync(
        ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_reports.Where(row => row.Item.ResumeId == resumeId).ToOldestFirstPage(page));
    }
}

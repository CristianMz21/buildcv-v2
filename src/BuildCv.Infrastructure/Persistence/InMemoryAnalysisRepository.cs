using System.Collections.Concurrent;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

namespace BuildCv.Infrastructure.Persistence;

// OLDEST first, matching AnalysisRepository: a score history is read forwards. See
// InMemoryResumeRepository for why the insertion counter exists.
public sealed class InMemoryAnalysisRepository : IAnalysisRepository
{
    private readonly ConcurrentDictionary<Guid, KeysetRow<Analysis>> _analyses = new();
    private long _sequence;

    public Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _analyses[analysis.Id.Value] = new KeysetRow<Analysis>(analysis, Interlocked.Increment(ref _sequence));
        return Task.CompletedTask;
    }

    public Task<Page<Analysis>> GetPageByResumeIdAsync(
        ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_analyses.Values
            .Where(row => row.Item.ResumeId.Value == resumeId.Value)
            .ToOldestFirstPage(page));
    }
}

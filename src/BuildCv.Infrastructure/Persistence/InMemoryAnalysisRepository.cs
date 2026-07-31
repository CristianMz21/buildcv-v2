using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

namespace BuildCv.Infrastructure.Persistence;

public sealed class InMemoryAnalysisRepository : IAnalysisRepository
{
    private readonly ConcurrentDictionary<Guid, Analysis> _analyses = new();

    public Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _analyses[analysis.Id.Value] = analysis;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Analysis>> GetByResumeIdAsync(ResumeId resumeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Analysis> analyses = _analyses.Values
            .Where(a => a.ResumeId.Value == resumeId.Value)
            .ToList();
        return Task.FromResult(analyses);
    }
}

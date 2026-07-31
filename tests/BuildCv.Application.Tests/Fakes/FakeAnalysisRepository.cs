namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

public sealed class FakeAnalysisRepository : IAnalysisRepository
{
    private readonly List<Analysis> _analyses = [];

    public Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default)
    {
        _analyses.Add(analysis);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Analysis>> GetByResumeIdAsync(ResumeId resumeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Analysis>>(_analyses.Where(a => a.ResumeId == resumeId).ToList());
}

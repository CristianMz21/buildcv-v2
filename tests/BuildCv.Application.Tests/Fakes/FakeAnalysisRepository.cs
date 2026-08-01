namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

// Oldest first, matching AnalysisRepository. See FakeResumeRepository for the counter.
public sealed class FakeAnalysisRepository : IAnalysisRepository
{
    private readonly List<KeysetRow<Analysis>> _analyses = [];
    private long _sequence;

    public Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default)
    {
        _analyses.Add(new KeysetRow<Analysis>(analysis, ++_sequence));
        return Task.CompletedTask;
    }

    public Task<Page<Analysis>> GetPageByResumeIdAsync(
        ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default) =>
        Task.FromResult(_analyses.Where(row => row.Item.ResumeId == resumeId).ToOldestFirstPage(page));
}

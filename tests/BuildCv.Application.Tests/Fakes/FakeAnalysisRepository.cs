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

    // Counts the calls that would have been a database round trip, for the same single assertion
    // FakeResumeRepository.ReadCount exists for — with one difference that matters here.
    //
    // GetAnalysisHistoryHandler authorizes by READING THE RESUME, so the resume counter moves on the
    // forbidden and malformed-cursor paths and cannot say anything about ordering. The store that must
    // stay untouched when a cursor is rejected is this one, so the counter has to live here too.
    public int ReadCount { get; private set; }

    public Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default)
    {
        _analyses.Add(new KeysetRow<Analysis>(analysis, ++_sequence));
        return Task.CompletedTask;
    }

    public Task<Analysis?> GetByIdAsync(AnalysisId id, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_analyses.FirstOrDefault(row => row.Item.Id == id)?.Item);
    }

    public Task<Page<Analysis>> GetPageByResumeIdAsync(
        ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_analyses.Where(row => row.Item.ResumeId == resumeId).ToOldestFirstPage(page));
    }
}

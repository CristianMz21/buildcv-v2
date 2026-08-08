namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Jobs;
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

    // Counts the INSERTS, which is the only thing that can tell de-duplication from a duplicate write.
    //
    // The response is identical either way: a re-score that reused the stored analysis and one that wrote
    // a second identical row both answer 200 with the same numbers, so every assertion about the response
    // shape passes in both worlds. This counter is the assertion that does not.
    public int WriteCount { get; private set; }

    public Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default)
    {
        WriteCount++;
        _analyses.Add(new KeysetRow<Analysis>(analysis, ++_sequence));
        return Task.CompletedTask;
    }

    public Task<Analysis?> GetByIdAsync(AnalysisId id, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_analyses.FirstOrDefault(row => row.Item.Id == id)?.Item);
    }

    // Newest first by the insertion counter, matching both real implementations.
    public Task<Analysis?> GetLatestByPairAsync(
        ResumeId resumeId, JobPostingId jobPostingId, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_analyses
            .Where(row => row.Item.ResumeId == resumeId && row.Item.JobPostingId == jobPostingId)
            .OrderByDescending(row => row.Position)
            .Select(row => row.Item)
            .FirstOrDefault());
    }

    public Task<Page<Analysis>> GetPageByResumeIdAsync(
        ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_analyses.Where(row => row.Item.ResumeId == resumeId).ToOldestFirstPage(page));
    }
}

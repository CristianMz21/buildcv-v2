using System.Collections.Concurrent;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Jobs;
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

    // NO IsLive filter here, unlike InMemoryOrganizationRepository — and the absence is the honest
    // answer rather than an omission.
    //
    // Organization and Account carry a domain Status, so their in-memory stores can reproduce the EF
    // tombstone from state the entity already holds. Analysis carries nothing of the kind: it is an
    // append-only fact with no Delete(), and the only writer of its DeletedAt column is the cascade in
    // ResumeRepository.DeleteAsync, which is an EF-side cross-aggregate write with no counterpart in
    // this store — IAnalysisRepository has no delete for one to hang off. A tombstoned row therefore
    // cannot exist here, and an IsLive that no state can ever falsify would be a guard that reads like a
    // guarantee while checking nothing.
    //
    // The divergence that leaves — an analysis outliving its deleted resume in this store but not in SQL
    // Server — is closed one layer up instead: GetAnalysisByIdHandler answers "Analysis not found." when
    // the resume behind an analysis is gone, so both providers return the same status AND the same
    // message. Api tests run on this store, so that equivalence is what makes them mean anything;
    // AnalysisRepositoryTests proves the EF half against a real database.
    public Task<Analysis?> GetByIdAsync(AnalysisId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _analyses.TryGetValue(id.Value, out var row);
        return Task.FromResult(row?.Item);
    }

    // Newest first by the insertion counter, matching AnalysisRepository's ordering on Seq. Ordering on
    // ScoredAt instead would diverge from SQL Server exactly when two rows share an instant, and the Api
    // suite runs on this store — a divergence here certifies behaviour production does not have.
    public Task<Analysis?> GetLatestByPairAsync(
        ResumeId resumeId, JobPostingId jobPostingId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_analyses.Values
            .Where(row => row.Item.ResumeId.Value == resumeId.Value
                && row.Item.JobPostingId.Value == jobPostingId.Value)
            .OrderByDescending(row => row.Position)
            .Select(row => row.Item)
            .FirstOrDefault());
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

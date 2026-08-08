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

    // NO IsLive filter here, unlike InMemoryOrganizationRepository — and the absence is still the honest
    // answer rather than an omission.
    //
    // Organization and Account carry a domain Status, so their in-memory stores can reproduce the EF
    // tombstone from state the entity already holds. Analysis carries nothing of the kind: it is an
    // append-only fact with no Delete(), and the only writer of its DeletedAt column is the cascade in
    // ResumeRepository.DeleteAsync. There is no state an IsLive could read here, so one would be a guard
    // that reads like a guarantee while checking nothing.
    //
    // What used to be missing was the cascade itself, not a filter: an analysis outlived its deleted
    // resume in this store and did not in SQL Server (issue #18). InMemoryResumeRepository.DeleteAsync
    // now calls RemoveAllDerivedFrom below, so the row is GONE rather than hidden — which is what
    // "no state to filter on" leaves as the only option, and what the EF side amounts to anyway once the
    // query filter has run.
    //
    // GetAnalysisByIdHandler's orphan branch stays regardless. It is the right answer for a row that is
    // genuinely orphaned, and it is what keeps the two providers agreeing on the MESSAGE and not only
    // the status. AnalysisRepositoryTests proves the EF half against a real database.
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

    // NOT ON IAnalysisRepository, and it must not go there. The port describes what a use case may ask
    // of a score history, and no use case deletes one — this is the in-memory counterpart of
    // ResumeRepository.CascadeToAnalysesAsync, which is likewise a repository-internal cross-aggregate
    // write with no handler behind it. Putting it on the port would hand every future caller a delete
    // the EF side does not offer either.
    //
    // Called only from InMemoryResumeRepository.DeleteAsync, which is why that store takes this one as a
    // constructor dependency rather than resolving IAnalysisRepository: the port has no such method, and
    // a cast would make the wiring in DependencyInjection.AddInMemoryPersistence a runtime detail
    // instead of a compile-time one.
    internal void RemoveAllDerivedFrom(ResumeId resumeId)
    {
        foreach (var row in _analyses.Values.Where(row => row.Item.ResumeId.Value == resumeId.Value))
            _analyses.TryRemove(row.Item.Id.Value, out _);
    }
}

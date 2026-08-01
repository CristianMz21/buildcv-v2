using System.Collections.Concurrent;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

namespace BuildCv.Infrastructure.Persistence;

// See InMemoryResumeRepository for why the insertion counter exists and why UpdateAsync must not
// renumber a row.
public sealed class InMemoryJobPostingRepository : IJobPostingRepository
{
    private readonly ConcurrentDictionary<Guid, KeysetRow<JobPosting>> _jobPostings = new();
    private long _sequence;

    public Task<JobPosting?> GetByIdAsync(JobPostingId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobPostings.TryGetValue(id.Value, out var row);
        return Task.FromResult(row?.Item);
    }

    public Task<Page<JobPosting>> GetPageByOwnerIdAsync(
        AccountId ownerId, PageRequest page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_jobPostings.Values
            .Where(row => row.Item.OwnerId.Value == ownerId.Value)
            .ToNewestFirstPage(page));
    }

    public Task<Page<JobPosting>> GetPageByOrganizationIdAsync(
        OrganizationId organizationId, PageRequest page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_jobPostings.Values
            .Where(row => row.Item.CompanyId is not null && row.Item.CompanyId.Value == organizationId.Value)
            .ToNewestFirstPage(page));
    }

    public Task AddAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobPostings[jobPosting.Id.Value] = NextRow(jobPosting);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobPostings.AddOrUpdate(
            jobPosting.Id.Value,
            _ => NextRow(jobPosting),
            (_, existing) => existing with { Item = jobPosting });
        return Task.CompletedTask;
    }

    private KeysetRow<JobPosting> NextRow(JobPosting jobPosting) =>
        new(jobPosting, Interlocked.Increment(ref _sequence));
}

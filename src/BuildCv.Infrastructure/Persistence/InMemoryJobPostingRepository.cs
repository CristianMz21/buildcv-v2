using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

namespace BuildCv.Infrastructure.Persistence;

public sealed class InMemoryJobPostingRepository : IJobPostingRepository
{
    private readonly ConcurrentDictionary<Guid, JobPosting> _jobPostings = new();

    public Task<JobPosting?> GetByIdAsync(JobPostingId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobPostings.TryGetValue(id.Value, out var jobPosting);
        return Task.FromResult(jobPosting);
    }

    public Task<IReadOnlyList<JobPosting>> GetByOwnerIdAsync(AccountId ownerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<JobPosting> jobPostings = _jobPostings.Values
            .Where(j => j.OwnerId.Value == ownerId.Value)
            .ToList();
        return Task.FromResult(jobPostings);
    }

    public Task<IReadOnlyList<JobPosting>> GetByOrganizationIdAsync(OrganizationId organizationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<JobPosting> jobPostings = _jobPostings.Values
            .Where(j => j.CompanyId is not null && j.CompanyId.Value == organizationId.Value)
            .ToList();
        return Task.FromResult(jobPostings);
    }

    public Task AddAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobPostings[jobPosting.Id.Value] = jobPosting;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobPostings[jobPosting.Id.Value] = jobPosting;
        return Task.CompletedTask;
    }
}

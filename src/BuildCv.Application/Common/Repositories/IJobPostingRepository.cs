namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

public interface IJobPostingRepository
{
    Task<JobPosting?> GetByIdAsync(JobPostingId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobPosting>> GetByOwnerIdAsync(AccountId ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobPosting>> GetByOrganizationIdAsync(OrganizationId organizationId, CancellationToken cancellationToken = default);
    Task AddAsync(JobPosting jobPosting, CancellationToken cancellationToken = default);
    Task UpdateAsync(JobPosting jobPosting, CancellationToken cancellationToken = default);
}

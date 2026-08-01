namespace BuildCv.Application.Common.Repositories;

using BuildCv.Application.Common.Pagination;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

public interface IJobPostingRepository
{
    Task<JobPosting?> GetByIdAsync(JobPostingId id, CancellationToken cancellationToken = default);

    // Both lists are newest first and paged. An organization's posting history is the least bounded
    // collection in the product — it only ever grows — so this is the port that must never grow an
    // unbounded overload back.
    Task<Page<JobPosting>> GetPageByOwnerIdAsync(AccountId ownerId, PageRequest page, CancellationToken cancellationToken = default);
    Task<Page<JobPosting>> GetPageByOrganizationIdAsync(OrganizationId organizationId, PageRequest page, CancellationToken cancellationToken = default);
    Task AddAsync(JobPosting jobPosting, CancellationToken cancellationToken = default);
    Task UpdateAsync(JobPosting jobPosting, CancellationToken cancellationToken = default);
}

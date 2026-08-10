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

    // Exists for account deletion and for nothing else, which is why there is no DELETE /v1/jobs/{id}
    // beside it: a posting a recruiter published is a record other people may have been scored against,
    // and removing one on request is a product decision nobody has made. Leaving when you close your
    // account is a different question with a different answer.
    //
    // It matters most for the CANDIDATE side of this table. POST /v1/job-offers/import writes a posting
    // owned by the candidate, and the set of vacancies somebody imported is a map of where they were
    // applying -- which is theirs, and has to leave with them.
    Task DeleteByOwnerAsync(AccountId ownerId, CancellationToken cancellationToken = default);
}

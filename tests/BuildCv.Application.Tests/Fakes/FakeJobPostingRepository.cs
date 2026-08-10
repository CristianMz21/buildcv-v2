namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

// See FakeResumeRepository for why the insertion counter exists.
public sealed class FakeJobPostingRepository : IJobPostingRepository
{
    private readonly List<KeysetRow<JobPosting>> _jobPostings = [];
    private long _sequence;

    public Task<JobPosting?> GetByIdAsync(JobPostingId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobPostings.FirstOrDefault(row => row.Item.Id == id)?.Item);

    public Task<Page<JobPosting>> GetPageByOwnerIdAsync(
        AccountId ownerId, PageRequest page, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobPostings.Where(row => row.Item.OwnerId == ownerId).ToNewestFirstPage(page));

    public Task<Page<JobPosting>> GetPageByOrganizationIdAsync(
        OrganizationId organizationId, PageRequest page, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobPostings.Where(row => row.Item.CompanyId == organizationId).ToNewestFirstPage(page));

    public Task AddAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        _jobPostings.Add(new KeysetRow<JobPosting>(jobPosting, ++_sequence));
        return Task.CompletedTask;
    }

    public Task UpdateAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        var index = _jobPostings.FindIndex(row => row.Item.Id == jobPosting.Id);
        if (index >= 0)
            _jobPostings[index] = _jobPostings[index] with { Item = jobPosting };
        return Task.CompletedTask;
    }

    public Task DeleteByOwnerAsync(AccountId ownerId, CancellationToken cancellationToken = default)
    {
        _jobPostings.RemoveAll(row => row.Item.OwnerId == ownerId);
        return Task.CompletedTask;
    }
}

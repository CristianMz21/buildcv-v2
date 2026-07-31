namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

public sealed class FakeJobPostingRepository : IJobPostingRepository
{
    private readonly List<JobPosting> _jobPostings = [];

    public Task<JobPosting?> GetByIdAsync(JobPostingId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobPostings.FirstOrDefault(j => j.Id == id));

    public Task<IReadOnlyList<JobPosting>> GetByOwnerIdAsync(AccountId ownerId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JobPosting>>(_jobPostings.Where(j => j.OwnerId == ownerId).ToList());

    public Task<IReadOnlyList<JobPosting>> GetByOrganizationIdAsync(OrganizationId organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JobPosting>>(_jobPostings.Where(j => j.CompanyId == organizationId).ToList());

    public Task AddAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        _jobPostings.Add(jobPosting);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        var index = _jobPostings.FindIndex(j => j.Id == jobPosting.Id);
        if (index >= 0)
            _jobPostings[index] = jobPosting;
        return Task.CompletedTask;
    }
}

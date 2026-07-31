namespace BuildCv.Application.Jobs;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

public sealed record GetJobPostingQuery(AccountId RequesterId, JobPostingId JobPostingId)
    : IQuery<Result<JobPosting>>;

public sealed class GetJobPostingHandler(
    IJobPostingRepository jobPostingRepository,
    IAccountRepository accountRepository,
    IOrganizationRepository organizationRepository)
    : IQueryHandler<GetJobPostingQuery, Result<JobPosting>>
{
    public async Task<Result<JobPosting>> Handle(GetJobPostingQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var jobPosting = await jobPostingRepository.GetByIdAsync(query.JobPostingId, cancellationToken);
            if (jobPosting is null)
                return Result<JobPosting>.Failure("Job posting not found.");

            if (jobPosting.Status == JobPostingStatus.Published)
                return Result<JobPosting>.Success(jobPosting);

            if (jobPosting.OwnerId == query.RequesterId)
                return Result<JobPosting>.Success(jobPosting);

            var requester = await accountRepository.GetByIdAsync(query.RequesterId, cancellationToken);
            if (requester?.Role == Role.Admin)
                return Result<JobPosting>.Success(jobPosting);

            if (jobPosting.CompanyId is not null)
            {
                var organization = await organizationRepository.GetByIdAsync(jobPosting.CompanyId, cancellationToken);
                if (organization?.Members.Any(m => m.AccountId == query.RequesterId) == true)
                    return Result<JobPosting>.Success(jobPosting);
            }

            return Result<JobPosting>.Failure("Forbidden.");
        }
        catch (DomainException ex)
        {
            return Result<JobPosting>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<JobPosting>.Failure(ex.Message);
        }
    }
}

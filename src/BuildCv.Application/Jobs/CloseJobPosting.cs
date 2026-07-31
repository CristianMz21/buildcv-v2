namespace BuildCv.Application.Jobs;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

public sealed record CloseJobPostingCommand(AccountId RequesterId, JobPostingId JobPostingId)
    : ICommand<Result<JobPosting>>;

public sealed class CloseJobPostingHandler(
    IJobPostingRepository jobPostingRepository,
    IOrganizationRepository organizationRepository)
    : ICommandHandler<CloseJobPostingCommand, Result<JobPosting>>
{
    public async Task<Result<JobPosting>> Handle(CloseJobPostingCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var jobPosting = await jobPostingRepository.GetByIdAsync(command.JobPostingId, cancellationToken);
            if (jobPosting is null)
                return Result<JobPosting>.Failure("Job posting not found.");

            if (!await IsAuthorizedAsync(jobPosting, command.RequesterId, cancellationToken))
                return Result<JobPosting>.Failure("Forbidden.");

            jobPosting.Close();
            await jobPostingRepository.UpdateAsync(jobPosting, cancellationToken);

            return Result<JobPosting>.Success(jobPosting);
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

    private async Task<bool> IsAuthorizedAsync(JobPosting jobPosting, AccountId requesterId, CancellationToken cancellationToken)
    {
        if (jobPosting.OwnerId == requesterId)
            return true;

        if (jobPosting.CompanyId is null)
            return false;

        var organization = await organizationRepository.GetByIdAsync(jobPosting.CompanyId, cancellationToken);
        var membership = organization?.Members.FirstOrDefault(m => m.AccountId == requesterId);
        return membership?.Role is MembershipRole.Owner or MembershipRole.Admin;
    }
}

namespace BuildCv.Application.Jobs;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

public sealed record CreateJobPostingCommand(
    AccountId RequesterId,
    string Title,
    string CompanyName,
    OrganizationId? CompanyId,
    string? Description) : ICommand<Result<JobPosting>>;

public sealed class CreateJobPostingHandler(
    IJobPostingRepository jobPostingRepository,
    IAccountRepository accountRepository,
    IOrganizationRepository organizationRepository)
    : ICommandHandler<CreateJobPostingCommand, Result<JobPosting>>
{
    public async Task<Result<JobPosting>> Handle(CreateJobPostingCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var requester = await accountRepository.GetByIdAsync(command.RequesterId, cancellationToken);
            if (requester is null)
                return Result<JobPosting>.Failure("Requester not found.");

            if (!requester.CanPostJobs)
                return Result<JobPosting>.Failure("Only recruiters can post jobs.");

            JobPosting jobPosting;
            if (command.CompanyId is not null)
            {
                var organization = await organizationRepository.GetByIdAsync(command.CompanyId, cancellationToken);
                if (organization is null)
                    return Result<JobPosting>.Failure("Organization not found.");

                var membership = organization.Members.FirstOrDefault(m => m.AccountId == command.RequesterId);
                if (membership?.Role is not (MembershipRole.Owner or MembershipRole.Admin))
                    return Result<JobPosting>.Failure("Forbidden.");

                jobPosting = JobPosting.CreateForOrganization(
                    command.RequesterId, command.CompanyId, command.Title, command.Description);
            }
            else
            {
                jobPosting = JobPosting.Create(
                    command.RequesterId,
                    command.Title,
                    OrganizationName.Create(command.CompanyName),
                    command.Description);
            }

            await jobPostingRepository.AddAsync(jobPosting, cancellationToken);

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
}

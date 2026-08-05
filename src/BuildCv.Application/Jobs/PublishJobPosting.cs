namespace BuildCv.Application.Jobs;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

public sealed record PublishJobPostingCommand(AccountId RequesterId, JobPostingId JobPostingId)
    : ICommand<Result<JobPosting>>;

public sealed class PublishJobPostingHandler(
    IJobPostingRepository jobPostingRepository,
    IOrganizationRepository organizationRepository,
    IAccountRepository accountRepository)
    : ICommandHandler<PublishJobPostingCommand, Result<JobPosting>>
{
    public async Task<Result<JobPosting>> Handle(PublishJobPostingCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var jobPosting = await jobPostingRepository.GetByIdAsync(command.JobPostingId, cancellationToken);
            if (jobPosting is null)
                return Result<JobPosting>.Failure("Job posting not found.");

            if (!await IsAuthorizedAsync(jobPosting, command.RequesterId, cancellationToken))
                return Result<JobPosting>.Failure("Forbidden.");

            jobPosting.Publish();
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
        // Publishing requires CanPostJobs, exactly as CreateJobPostingHandler requires it to create.
        // Ownership alone is no longer sufficient, and that is the security fix this PR must carry:
        // POST /job-offers/import gives a candidate ownership of a Draft posting, which is the first
        // posting an account WITHOUT CanPostJobs can own -- CreateJobPostingHandler refuses everyone
        // else. A published posting is scored for ANY authenticated caller (ScoreResume admits
        // Status == Published unconditionally), so without this gate a candidate could publish their
        // private offer with one call and it would stop being private.
        //
        // This is checked FIRST and applies to every path below, so it also covers the org path: an
        // org Owner/Admin whose account is not a recruiter (reachable -- any active account can found
        // an organization and any Owner/Admin can add another as Admin, neither gated on role) can no
        // longer publish an org posting a recruiter colleague drafted, and a locked or suspended
        // recruiter loses it too. That is a real behaviour change, not a no-op; it is pinned by tests.
        var requester = await accountRepository.GetByIdAsync(requesterId, cancellationToken);
        if (requester is null || !requester.CanPostJobs)
            return false;

        if (jobPosting.OwnerId == requesterId)
            return true;

        if (jobPosting.CompanyId is null)
            return false;

        var organization = await organizationRepository.GetByIdAsync(jobPosting.CompanyId, cancellationToken);
        var membership = organization?.Members.FirstOrDefault(m => m.AccountId == requesterId);
        return membership?.Role is MembershipRole.Owner or MembershipRole.Admin;
    }
}

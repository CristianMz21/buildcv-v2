namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

/// <summary>
/// Closes an account and removes everything it owns.
/// </summary>
/// <remarks>
/// <para>
/// A product that holds somebody's full employment history, their phone number and the list of vacancies
/// they were quietly applying to has to let them leave. Until this existed a candidate could delete one CV
/// at a time and never the account behind it, so the address, the password hash and every posting they had
/// imported stayed indefinitely.
/// </para>
/// <para>
/// ORDER IS THE SAFETY PROPERTY. The account is tombstoned LAST. Everything before it is idempotent and
/// re-runnable, so a failure part way through leaves an account that still authenticates and can be asked
/// to delete again; tombstoning first would leave the caller locked out with their CVs still stored, and
/// nothing in this API could reach them afterwards. There is no transaction spanning these repositories —
/// they are separate aggregates by design — so the ordering is the whole guarantee.
/// </para>
/// <para>
/// WHAT LEAVES: every resume, and with each one the analyses and readability reports derived from it
/// (<c>ResumeRepository.DeleteAsync</c> already cascades — a readability message quotes the candidate's own
/// bullet points). Every job posting they own, because <c>POST /v1/job-offers/import</c> makes those a map
/// of where somebody was applying. Every refresh token. Then the account itself, whose tombstone is what
/// frees the address for re-registration through the filtered unique index on <c>EmailHash</c>.
/// </para>
/// <para>
/// WHAT DOES NOT: an organization with other members. Deleting one would destroy data belonging to people
/// who did not ask to leave, and this API has no way to transfer ownership, so the account is refused with
/// a message naming the organization and the one path out — remove the other members first, or have
/// another owner remove you. An organization where this account is the ONLY member is deleted with it,
/// since nothing is left to inherit it.
/// </para>
/// </remarks>
public sealed record DeleteAccountCommand(AccountId AccountId, string CurrentPassword) : ICommand<Result>;

public sealed class DeleteAccountHandler(
    IAccountRepository accountRepository,
    IPasswordHasher passwordHasher,
    IResumeRepository resumeRepository,
    IJobPostingRepository jobPostingRepository,
    IOrganizationRepository organizationRepository,
    IRefreshTokenRepository refreshTokenRepository)
    : ICommandHandler<DeleteAccountCommand, Result>
{
    public async Task<Result> Handle(DeleteAccountCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var account = await accountRepository.GetByIdAsync(command.AccountId, cancellationToken);
            if (account is null)
                return Result.Failure("Account not found.");

            // The same check /auth/change-password makes, and for a stronger reason: an access token is a
            // bearer credential, so without this a stolen one would be enough to erase somebody's entire
            // employment history with no way back. The refusal deliberately reuses that endpoint's
            // wording rather than saying "wrong password for deletion" -- a message that varies by
            // endpoint tells an attacker which of them they reached.
            if (!passwordHasher.Verify(command.CurrentPassword, account.Password.Hash))
                return Result.Failure("Current password is incorrect.");

            // Checked BEFORE anything is destroyed. A refusal has to leave the account exactly as it was,
            // and a caller who is told "remove the other members first" must still have their CVs when
            // they come back.
            var organizations = await organizationRepository.GetByMemberIdAsync(command.AccountId, cancellationToken);
            var blocking = organizations.FirstOrDefault(organization => IsBlockedBy(organization, command.AccountId));
            if (blocking is not null)
            {
                return Result.Failure(
                    $"You are the only owner of \"{blocking.Name.Value}\", which has other members. "
                    + "Remove them, or have another owner remove you, before closing your account.");
            }

            await DeleteResumesAsync(command.AccountId, cancellationToken);
            await jobPostingRepository.DeleteByOwnerAsync(command.AccountId, cancellationToken);
            await LeaveOrganizationsAsync(organizations, command.AccountId, cancellationToken);
            await refreshTokenRepository.RevokeAllForAccountAsync(command.AccountId, cancellationToken);

            account.Delete();
            await accountRepository.UpdateAsync(account, cancellationToken);

            return Result.Success();
        }
        catch (DomainException ex)
        {
            return Result.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure(ex.Message);
        }
    }

    // Sole owner AND somebody else is still there. Sole owner of a solo organization is not blocking:
    // there is nobody to strand.
    private static bool IsBlockedBy(Organization organization, AccountId accountId) =>
        organization.Members.Count > 1
        && organization.Members.Any(member =>
            member.AccountId == accountId && member.Role == MembershipRole.Owner)
        && organization.Members.Count(member => member.Role == MembershipRole.Owner) == 1;

    // Paged, because the port has no unbounded list and must not grow one. The page is re-requested from
    // the FIRST cursor each time rather than walked forwards: every iteration deletes what it just read,
    // so the rows a cursor pointed past are gone and advancing would skip the ones that took their place.
    private async Task DeleteResumesAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await resumeRepository.GetPageByOwnerIdAsync(
                accountId, PageRequest.Create(PageRequest.MaxLimit, cursor: null).Value!, cancellationToken);

            if (page.Items.Count == 0)
                return;

            foreach (var resume in page.Items)
                await resumeRepository.DeleteAsync(resume.Id, cancellationToken);
        }
    }

    private async Task LeaveOrganizationsAsync(
        IReadOnlyList<Organization> organizations, AccountId accountId, CancellationToken cancellationToken)
    {
        foreach (var organization in organizations)
        {
            // The last member switches the organization off rather than leaving it memberless. An
            // organization nobody belongs to cannot be joined, renamed or deleted through this API —
            // every one of those routes authorizes against a membership — so it would be unreachable
            // state holding a name and a slug that nobody could ever reclaim.
            if (organization.Members.Count == 1)
                organization.Delete();
            else
                organization.RemoveMember(accountId);

            await organizationRepository.UpdateAsync(organization, cancellationToken);
        }
    }
}

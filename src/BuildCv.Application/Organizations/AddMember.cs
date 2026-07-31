namespace BuildCv.Application.Organizations;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

public sealed record AddMemberCommand(
    AccountId RequesterId,
    OrganizationId OrganizationId,
    AccountId NewMemberId,
    MembershipRole Role) : ICommand<Result<Organization>>;

public sealed class AddMemberHandler(
    IOrganizationRepository organizationRepository,
    IAccountRepository accountRepository)
    : ICommandHandler<AddMemberCommand, Result<Organization>>
{
    public async Task<Result<Organization>> Handle(AddMemberCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var organization = await organizationRepository.GetByIdAsync(command.OrganizationId, cancellationToken);
            if (organization is null)
                return Result<Organization>.Failure("Organization not found.");

            var requesterMembership = organization.Members.FirstOrDefault(m => m.AccountId == command.RequesterId);
            if (requesterMembership?.Role is not (MembershipRole.Owner or MembershipRole.Admin))
                return Result<Organization>.Failure("Forbidden.");

            var newMember = await accountRepository.GetByIdAsync(command.NewMemberId, cancellationToken);
            if (newMember is null || newMember.Status != AccountStatus.Active)
                return Result<Organization>.Failure("Member account not found or not active.");

            organization.AddMember(command.NewMemberId, command.Role);
            await organizationRepository.UpdateAsync(organization, cancellationToken);

            return Result<Organization>.Success(organization);
        }
        catch (DomainException ex)
        {
            return Result<Organization>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<Organization>.Failure(ex.Message);
        }
    }
}

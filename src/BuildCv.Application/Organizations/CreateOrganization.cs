namespace BuildCv.Application.Organizations;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

public sealed record CreateOrganizationCommand(AccountId RequesterId, string Name, string Slug)
    : ICommand<Result<Organization>>;

public sealed class CreateOrganizationHandler(
    IOrganizationRepository organizationRepository,
    IAccountRepository accountRepository)
    : ICommandHandler<CreateOrganizationCommand, Result<Organization>>
{
    public async Task<Result<Organization>> Handle(CreateOrganizationCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var requester = await accountRepository.GetByIdAsync(command.RequesterId, cancellationToken);
            if (requester is null)
                return Result<Organization>.Failure("Requester not found.");

            if (requester.Status != AccountStatus.Active)
                return Result<Organization>.Failure("Only active accounts can create organizations.");

            var slug = Slug.Create(command.Slug);
            if (await organizationRepository.GetBySlugAsync(slug, cancellationToken) is not null)
                return Result<Organization>.Failure("Slug is already taken.");

            var organization = Organization.Create(
                OrganizationName.Create(command.Name), slug, command.RequesterId);
            await organizationRepository.AddAsync(organization, cancellationToken);

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

namespace BuildCv.Application.Organizations;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

public sealed record GetOrganizationQuery(AccountId RequesterId, OrganizationId OrganizationId)
    : IQuery<Result<Organization>>;

public sealed class GetOrganizationHandler(IOrganizationRepository organizationRepository)
    : IQueryHandler<GetOrganizationQuery, Result<Organization>>
{
    public async Task<Result<Organization>> Handle(GetOrganizationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var organization = await organizationRepository.GetByIdAsync(query.OrganizationId, cancellationToken);
            if (organization is null)
                return Result<Organization>.Failure("Organization not found.");

            if (!organization.Members.Any(m => m.AccountId == query.RequesterId))
                return Result<Organization>.Failure("Forbidden.");

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

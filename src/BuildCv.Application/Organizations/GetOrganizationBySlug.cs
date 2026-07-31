namespace BuildCv.Application.Organizations;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

public sealed record GetOrganizationBySlugQuery(AccountId RequesterId, string Slug)
    : IQuery<Result<Organization>>;

public sealed class GetOrganizationBySlugHandler(IOrganizationRepository organizationRepository)
    : IQueryHandler<GetOrganizationBySlugQuery, Result<Organization>>
{
    public async Task<Result<Organization>> Handle(GetOrganizationBySlugQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var slug = Slug.Create(query.Slug);
            var organization = await organizationRepository.GetBySlugAsync(slug, cancellationToken);
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

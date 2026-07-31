namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Organizations;

public interface IOrganizationRepository
{
    Task<Organization?> GetByIdAsync(OrganizationId id, CancellationToken cancellationToken = default);
    Task<Organization?> GetBySlugAsync(Slug slug, CancellationToken cancellationToken = default);
    Task AddAsync(Organization organization, CancellationToken cancellationToken = default);
    Task UpdateAsync(Organization organization, CancellationToken cancellationToken = default);
}

using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Organizations;

namespace BuildCv.Infrastructure.Persistence;

public sealed class InMemoryOrganizationRepository : IOrganizationRepository
{
    private readonly ConcurrentDictionary<Guid, Organization> _organizations = new();

    public Task<Organization?> GetByIdAsync(OrganizationId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _organizations.TryGetValue(id.Value, out var organization);
        return Task.FromResult(organization);
    }

    public Task<Organization?> GetBySlugAsync(Slug slug, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var organization = _organizations.Values.FirstOrDefault(
            o => string.Equals(o.Slug.Value, slug.Value, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(organization);
    }

    public Task AddAsync(Organization organization, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _organizations[organization.Id.Value] = organization;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Organization organization, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _organizations[organization.Id.Value] = organization;
        return Task.CompletedTask;
    }
}

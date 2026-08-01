using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Organizations;

namespace BuildCv.Infrastructure.Persistence;

// Filters Status == Deleted out of both lookups for the same reason InMemoryAccountRepository does: the
// EF repository writes the DeletedAt tombstone alongside Organization.Delete()'s status change, and the
// filtered unique index on Slug then releases the public handle. See that file for the full argument.
public sealed class InMemoryOrganizationRepository : IOrganizationRepository
{
    private readonly ConcurrentDictionary<Guid, Organization> _organizations = new();

    public Task<Organization?> GetByIdAsync(OrganizationId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _organizations.TryGetValue(id.Value, out var organization);
        return Task.FromResult(IsLive(organization) ? organization : null);
    }

    public Task<Organization?> GetBySlugAsync(Slug slug, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var organization = _organizations.Values.FirstOrDefault(
            o => IsLive(o) && string.Equals(o.Slug.Value, slug.Value, StringComparison.OrdinalIgnoreCase));
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

    private static bool IsLive(Organization? organization) =>
        organization is { Status: not OrganizationStatus.Deleted };
}

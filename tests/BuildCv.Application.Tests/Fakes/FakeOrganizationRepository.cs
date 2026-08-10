namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

public sealed class FakeOrganizationRepository : IOrganizationRepository
{
    private readonly List<Organization> _organizations = [];

    public Task<Organization?> GetByIdAsync(OrganizationId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_organizations.FirstOrDefault(o => o.Id == id));

    public Task<Organization?> GetBySlugAsync(Slug slug, CancellationToken cancellationToken = default) =>
        Task.FromResult(_organizations.FirstOrDefault(o => o.Slug == slug));

    public Task AddAsync(Organization organization, CancellationToken cancellationToken = default)
    {
        _organizations.Add(organization);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Organization organization, CancellationToken cancellationToken = default)
    {
        var index = _organizations.FindIndex(o => o.Id == organization.Id);
        if (index >= 0)
            _organizations[index] = organization;
        return Task.CompletedTask;
    }

    // Deleted organizations are excluded, matching both real stores: their query filter and their IsLive
    // check make a tombstoned row invisible, and a fake that still returned one would let a test pass
    // against behaviour production does not have.
    public Task<IReadOnlyList<Organization>> GetByMemberIdAsync(
        AccountId accountId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Organization> matches =
        [
            .. _organizations
                .Where(organization => organization.Status != OrganizationStatus.Deleted
                    && organization.Members.Any(member => member.AccountId == accountId))
        ];

        return Task.FromResult(matches);
    }
}

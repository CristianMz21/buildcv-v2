using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Organizations, against SQL Server. Memberships are an owned collection and load with the root.
//
// Slug is analytical, plaintext and indexed, so unlike Account.Email it is looked up directly. The
// difference is deliberate: a slug is the public handle in a URL, an address is the person.
internal sealed class OrganizationRepository : IOrganizationRepository
{
    private readonly BuildCvDbContext _context;
    private readonly TimeProvider _timeProvider;

    public OrganizationRepository(BuildCvDbContext context, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<Organization?> GetByIdAsync(OrganizationId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return await _context.Organizations.AsTracking()
            .FirstOrDefaultAsync(organization => organization.Id == id, cancellationToken);
    }

    // AsTracking as well: AddMember and RemoveMember reach the aggregate through this lookup just as
    // often as through the id one.
    public async Task<Organization?> GetBySlugAsync(Slug slug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slug);
        return await _context.Organizations.AsTracking()
            .FirstOrDefaultAsync(organization => organization.Slug == slug, cancellationToken);
    }

    public async Task AddAsync(Organization organization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);
        _context.Organizations.Add(organization);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    public async Task UpdateAsync(Organization organization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);

        var entry = _context.Entry(organization);

        // See AccountRepository.UpdateAsync for why the detached path cannot verify a rowversion.
        if (entry.State is EntityState.Detached)
            entry = _context.Organizations.Update(organization);

        // Organization.Delete() sets Status = Deleted and knows nothing about tombstones; this is the
        // other half of that delete. See TombstoneExtensions for why it is not routed through Remove().
        if (organization.Status is OrganizationStatus.Deleted)
            entry.MarkTombstoned(_timeProvider.GetUtcNow());

        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }
}

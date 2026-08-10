using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Job postings, against SQL Server. Requirements and responsibilities are owned collections, so they
// load with the posting without an Include.
//
// No tombstone translation here: JobPosting exposes Close() and Archive(), which are lifecycle states a
// posting is meant to be readable in, not deletes. Adding a Delete() to the aggregate later means adding
// the translation AccountRepository and OrganizationRepository carry.
internal sealed class JobPostingRepository : IJobPostingRepository
{
    private readonly BuildCvDbContext _context;

    public JobPostingRepository(BuildCvDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<JobPosting?> GetByIdAsync(JobPostingId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return await _context.JobPostings.AsTracking()
            .FirstOrDefaultAsync(posting => posting.Id == id, cancellationToken);
    }

    public Task<Page<JobPosting>> GetPageByOwnerIdAsync(
        AccountId ownerId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(page);

        return _context.JobPostings
            .Where(posting => posting.OwnerId == ownerId)
            .ToNewestFirstPageAsync(page, cancellationToken);
    }

    public Task<Page<JobPosting>> GetPageByOrganizationIdAsync(
        OrganizationId organizationId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(page);

        return _context.JobPostings
            .Where(posting => posting.CompanyId == organizationId)
            .ToNewestFirstPageAsync(page, cancellationToken);
    }

    public async Task AddAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobPosting);
        _context.JobPostings.Add(jobPosting);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    public async Task UpdateAsync(JobPosting jobPosting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobPosting);

        // See TrackedAggregateExtensions for why a detached aggregate is refused rather than re-attached.
        _context.RequireTracked(jobPosting);

        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    // Remove(), not ExecuteDeleteAsync, and for the reason RevokeAllForAccountAsync gives: the latter
    // bypasses SaveChanges, so the audit interceptor never writes the DeletedAt tombstone and a soft
    // delete silently becomes a hard one. A tombstone is what the filtered indexes read.
    //
    // Loaded with AsTracking and no page: this runs once, when an account leaves, and it has to reach
    // EVERY posting they own. A paged loop here would be a delete that stops after twenty.
    public async Task DeleteByOwnerAsync(AccountId ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);

        var owned = await _context.JobPostings.AsTracking()
            .Where(posting => posting.OwnerId == ownerId)
            .ToListAsync(cancellationToken);

        if (owned.Count == 0)
            return;

        _context.JobPostings.RemoveRange(owned);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }
}

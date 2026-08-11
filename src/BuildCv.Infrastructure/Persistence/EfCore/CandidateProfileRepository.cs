using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// The candidate's master data, against SQL Server.
//
// No Include calls, for the reason ResumeRepository states: every child collection here is an OWNED
// type and owned navigations load with their principal, so an Include would be a no-op that reads like
// a requirement.
internal sealed class CandidateProfileRepository : ICandidateProfileRepository
{
    private readonly BuildCvDbContext _context;

    public CandidateProfileRepository(BuildCvDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    // AsSplitQuery, and on this aggregate it is not a judgement call the way it was on Resume.
    //
    // Ten owned collections in one statement is ten LEFT JOINs onto the same principal, and SQL Server
    // ships their CARTESIAN PRODUCT — EF de-duplicates on materialization, so every functional test
    // passes either way and the cost is entirely in rows the server builds, sorts and sends. On a resume
    // that product is at least bounded by what fits in a document a human wrote. A PROFILE HAS NO SUCH
    // BOUND BY DESIGN: it is the superset of everything the candidate has ever done, it is written to
    // from several directions over years, and nothing in the aggregate caps any collection. The join
    // form's cost is therefore the product of ten unbounded counts.
    //
    // AsTracking because every caller that reads a profile is about to write to it — importing a CV,
    // accepting an edit — and UpdateAsync refuses a detached aggregate.
    public async Task<CandidateProfile?> GetByOwnerIdAsync(
        AccountId ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);

        return await _context.CandidateProfiles.AsTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(profile => profile.OwnerId == ownerId, cancellationToken);
    }

    // The unique filtered index on OwnerId is what makes "one profile per account" true, and it is
    // reached rather than pre-empted: two concurrent imports both read "no profile yet", both insert,
    // and the loser gets a unique-violation instead of a silent second copy of the candidate's history.
    // SaveTranslatingFailuresAsync turns that into the same typed failure every other duplicate here
    // produces.
    public async Task AddAsync(CandidateProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _context.CandidateProfiles.Add(profile);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    public async Task UpdateAsync(CandidateProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Refused rather than re-attached, exactly as on Resume and for the same reason: Update() on a
        // detached profile marks all ten owned collections Added, because their shadow keys are unset
        // too — every entry would be inserted a second time. See TrackedAggregateExtensions.
        _context.RequireTracked(profile);

        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }
}

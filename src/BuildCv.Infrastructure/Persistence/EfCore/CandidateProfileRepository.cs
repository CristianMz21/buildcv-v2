using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Persistence.Conventions;
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

        return await ByOwnerQuery(_context, ownerId).FirstOrDefaultAsync(cancellationToken);
    }

    // The ids come out of the CHANGE TRACKER, not out of a second query, and that is what makes the
    // positional alignment ResumeItemIds promises true rather than hoped for — the same reasoning as
    // ResumeRepository.GetByIdWithItemIdsAsync, and it is worth repeating here because the two are
    // asked the same way across a shared enum. ByOwnerQuery is AsTracking, so each owned entry EF
    // materialized into profile.Skills is the very instance the tracker holds a shadow key for. Walking
    // the aggregate's own list and asking the tracker per element therefore reads ids in the
    // aggregate's order by construction, with no ORDER BY to keep in sync.
    public async Task<CandidateProfileWithItemIds?> GetByOwnerIdWithItemIdsAsync(
        AccountId ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);

        var profile = await ByOwnerQuery(_context, ownerId).FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
            return null;

        return new CandidateProfileWithItemIds(profile, new ResumeItemIds(new Dictionary<ResumeSection, IReadOnlyList<int>>
        {
            [ResumeSection.Experiences] = KeysOf(profile.Experiences),
            [ResumeSection.Educations] = KeysOf(profile.Educations),
            [ResumeSection.Skills] = KeysOf(profile.Skills),
            [ResumeSection.Projects] = KeysOf(profile.Projects),
            [ResumeSection.Certificates] = KeysOf(profile.Certificates),
            [ResumeSection.Languages] = KeysOf(profile.Languages),
            [ResumeSection.Awards] = KeysOf(profile.Awards),
            [ResumeSection.Publications] = KeysOf(profile.Publications),
            [ResumeSection.Interests] = KeysOf(profile.Interests),
            [ResumeSection.References] = KeysOf(profile.References)
        }));
    }

    private IReadOnlyList<int> KeysOf<T>(IReadOnlyList<T> items)
        where T : class =>
        [.. items.Select(item => (int)_context.Entry(item).Property(ChildTable.Key).CurrentValue!)];

    internal static IQueryable<CandidateProfile> ByOwnerQuery(
        BuildCvDbContext context, AccountId ownerId) =>
        context.CandidateProfiles.AsTracking()
            .AsSplitQuery()
            .Where(profile => profile.OwnerId == ownerId);

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

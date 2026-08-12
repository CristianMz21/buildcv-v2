namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Candidates;
using BuildCv.Domain.Identity;

/// <summary>
/// The candidate's master data — one profile per account.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no paged list here, and its absence is the point.</b> Every other collection port in this
/// application pages because its size is user-controlled and "list them all" is a query the caller
/// cannot bound. A profile is looked up by its owner and there is exactly one, so the question this port
/// answers is "the profile", not "which profiles".
/// </para>
/// <para>
/// The collections INSIDE it are unbounded by design — it is a superset of everything the candidate has
/// done, not a document with a page limit — which is why it is loaded whole and why nothing here offers
/// a partial read. A generator selecting from it needs all of it; that is what selecting means.
/// </para>
/// </remarks>
public interface ICandidateProfileRepository
{
    Task<CandidateProfile?> GetByOwnerIdAsync(AccountId ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same profile, plus the identity of every entry in its ten collections.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="GetByOwnerIdAsync"/> ON PURPOSE, and not merged into it — the mirror of
    /// <c>IResumeRepository.GetByIdWithItemIdsAsync</c>. Both loads materialize tracked; what the
    /// with-ids form ADDS is a per-entry walk of every item's shadow key, and the reads that only
    /// consult a profile — a generator selecting from it, a merge that only appends — are spared that
    /// walk, not tracking. This one is for the two operations that must name one entry out of many:
    /// removing and replacing.
    /// </remarks>
    Task<CandidateProfileWithItemIds?> GetByOwnerIdWithItemIdsAsync(
        AccountId ownerId, CancellationToken cancellationToken = default);

    Task AddAsync(CandidateProfile profile, CancellationToken cancellationToken = default);

    Task UpdateAsync(CandidateProfile profile, CancellationToken cancellationToken = default);
}

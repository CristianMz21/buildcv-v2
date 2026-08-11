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

    Task AddAsync(CandidateProfile profile, CancellationToken cancellationToken = default);

    Task UpdateAsync(CandidateProfile profile, CancellationToken cancellationToken = default);
}

using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Identity;

namespace BuildCv.Infrastructure.Persistence;

// The development and Api-test counterpart of CandidateProfileRepository.
//
// KEYED BY OWNER rather than by profile id, which is the whole shape of this aggregate: there is one
// per account, the port has no list method, and the only read is "the profile of this account". A
// dictionary keyed by the profile's own id would need a scan to answer that.
//
// No insertion counter, unlike every other store here — that exists to stand in for the bigint IDENTITY
// Seq column so paged reads behave as SQL Server does, and nothing pages this table.
public sealed class InMemoryCandidateProfileRepository : ICandidateProfileRepository
{
    private readonly ConcurrentDictionary<Guid, CandidateProfile> _profiles = new();

    // Exposed for the Api tests, matching the other in-memory stores: what a test needs to know is that
    // a request WROTE, and a count says that without arranging an owner to ask with.
    public int Count => _profiles.Count;

    // Returns the STORED INSTANCE, exactly as InMemoryResumeRepository does, so a handler that mutates
    // the aggregate and never calls UpdateAsync still appears to have saved. That is a real divergence
    // from the EF store and it is the pre-existing bargain of this whole store; it is written down here
    // so the next reader does not discover it from a test that passes for the wrong reason.
    public Task<CandidateProfile?> GetByOwnerIdAsync(
        AccountId ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        cancellationToken.ThrowIfCancellationRequested();

        _profiles.TryGetValue(ownerId.Value, out var profile);
        return Task.FromResult<CandidateProfile?>(profile);
    }

    // REFUSES A SECOND PROFILE FOR ONE ACCOUNT, because SQL Server does: the filtered unique index on
    // OwnerId turns that insert into a DuplicateKeyException, and the Api suite runs entirely on this
    // store. A dictionary that quietly overwrote instead would certify behaviour production does not
    // have — and the behaviour it would certify is the loss of a candidate's entire history.
    public Task AddAsync(CandidateProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_profiles.TryAdd(profile.OwnerId.Value, profile))
            throw new DuplicateKeyException("A record with the same unique value already exists.");

        return Task.CompletedTask;
    }

    public Task UpdateAsync(CandidateProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        _profiles[profile.OwnerId.Value] = profile;
        return Task.CompletedTask;
    }
}

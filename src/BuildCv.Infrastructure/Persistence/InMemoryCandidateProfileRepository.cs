using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
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
    private readonly ConcurrentDictionary<Guid, ItemIdMap> _itemIds = new();

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

    // STANDS IN FOR THE SHADOW KEY EF ASSIGNS EACH OWNED ROW, for the same reason InMemoryResumeRepository
    // does: the Api suite runs entirely on this store, so a profile whose entries could not be told apart
    // here would certify a delete-by-id behaviour SQL Server does have and this one does not.
    //
    // Identity is by REFERENCE, which is the closest analogue available. EF distinguishes two entries by
    // their row, not by their value; this store holds the aggregate itself, so the object is the row.
    public Task<CandidateProfileWithItemIds?> GetByOwnerIdWithItemIdsAsync(
        AccountId ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_profiles.TryGetValue(ownerId.Value, out var profile))
            return Task.FromResult<CandidateProfileWithItemIds?>(null);

        var map = _itemIds.GetOrAdd(ownerId.Value, _ => new ItemIdMap());

        var ids = map.Assign(new Dictionary<ResumeSection, IReadOnlyList<object>>
        {
            [ResumeSection.Experiences] = profile.Experiences,
            [ResumeSection.Educations] = profile.Educations,
            [ResumeSection.Skills] = profile.Skills,
            [ResumeSection.Projects] = profile.Projects,
            [ResumeSection.Certificates] = profile.Certificates,
            [ResumeSection.Languages] = profile.Languages,
            [ResumeSection.Awards] = profile.Awards,
            [ResumeSection.Publications] = profile.Publications,
            [ResumeSection.Interests] = profile.Interests,
            [ResumeSection.References] = profile.References
        });

        return Task.FromResult<CandidateProfileWithItemIds?>(new CandidateProfileWithItemIds(profile, ids));
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

    /// <summary>
    /// Hands each collection entry of one profile a number and remembers it for as long as that entry
    /// stays on the aggregate.
    /// </summary>
    /// <remarks>
    /// The portrait twin of the class that does the same job for resumes in InMemoryResumeRepository;
    /// this one is keyed per owner, matching this store's shape. Seeded at 1 like every other surrogate
    /// in this store, so a zero can never be mistaken for an id. Numbers are never reused: an entry that
    /// is removed takes its id out of circulation, which is what stops a stale client from deleting
    /// whatever landed in the position its id used to occupy.
    /// </remarks>
    private sealed class ItemIdMap
    {
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Lock _gate = new();
        private int _next;

        public ResumeItemIds Assign(IReadOnlyDictionary<ResumeSection, IReadOnlyList<object>> sections)
        {
            lock (_gate)
            {
                var assigned = new Dictionary<ResumeSection, IReadOnlyList<int>>(sections.Count);
                var live = new HashSet<object>(ReferenceEqualityComparer.Instance);

                foreach (var (section, items) in sections)
                {
                    var ids = new int[items.Count];

                    for (var position = 0; position < items.Count; position++)
                    {
                        var item = items[position];
                        live.Add(item);

                        if (!_ids.TryGetValue(item, out var id))
                        {
                            id = ++_next;
                            _ids[item] = id;
                        }

                        ids[position] = id;
                    }

                    assigned[section] = ids;
                }

                // Entries the aggregate no longer holds are dropped here rather than left behind. This
                // store outlives a request, so without it every removed bullet point would keep its
                // object alive for the life of the process.
                foreach (var stale in _ids.Keys.Where(key => !live.Contains(key)).ToList())
                    _ids.Remove(stale);

                return new ResumeItemIds(assigned);
            }
        }
    }
}

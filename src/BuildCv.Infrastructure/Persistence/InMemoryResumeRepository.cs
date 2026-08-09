using System.Collections.Concurrent;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

namespace BuildCv.Infrastructure.Persistence;

// The counter below stands in for the bigint IDENTITY Seq column, and it is not a convenience. Without
// a monotonic insert order a dictionary answers the list port in whatever order its buckets happen to
// be in — which is neither "newest first" nor even stable between two calls — and the Api tests run
// against this provider, so an unordered answer here would certify page behavior SQL Server does not
// produce.
//
// Assigned on ADD only. An UPDATE does not move a row in the clustered index, so it must not move one
// here either: otherwise editing an old resume would teleport it to the top of the list, past a cursor
// a client was already walking.
public sealed class InMemoryResumeRepository(InMemoryAnalysisRepository analyses) : IResumeRepository
{
    private readonly ConcurrentDictionary<Guid, KeysetRow<Resume>> _resumes = new();
    private readonly ConcurrentDictionary<Guid, ItemIdMap> _itemIds = new();
    private long _sequence;

    public Task<Resume?> GetByIdAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes.TryGetValue(id.Value, out var row);
        return Task.FromResult(row?.Item);
    }

    // STANDS IN FOR THE SHADOW KEY EF ASSIGNS EACH OWNED ROW, for the same reason the sequence above
    // stands in for the Seq column: the Api suite runs entirely on this store, so a resume whose entries
    // could not be told apart here would certify a delete-by-id behaviour SQL Server does have and this
    // one does not.
    //
    // Identity is by REFERENCE, which is the closest analogue available. EF distinguishes two entries by
    // their row, not by their value; this store holds the aggregate itself, so the object is the row. It
    // is what lets a candidate delete the second of two identical awards — the case value equality
    // cannot express, and the reason ResumeItemIds exists at all.
    public Task<ResumeWithItemIds?> GetByIdWithItemIdsAsync(
        ResumeId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_resumes.TryGetValue(id.Value, out var row))
            return Task.FromResult<ResumeWithItemIds?>(null);

        var resume = row.Item;
        var map = _itemIds.GetOrAdd(id.Value, _ => new ItemIdMap());

        var ids = map.Assign(new Dictionary<ResumeSection, IReadOnlyList<object>>
        {
            [ResumeSection.Experiences] = resume.Experiences,
            [ResumeSection.Educations] = resume.Educations,
            [ResumeSection.Skills] = resume.Skills,
            [ResumeSection.Projects] = resume.Projects,
            [ResumeSection.Certificates] = resume.Certificates,
            [ResumeSection.Languages] = resume.Languages,
            [ResumeSection.Awards] = resume.Awards,
            [ResumeSection.Publications] = resume.Publications,
            [ResumeSection.Interests] = resume.Interests,
            [ResumeSection.References] = resume.References
        });

        return Task.FromResult<ResumeWithItemIds?>(new ResumeWithItemIds(resume, ids));
    }

    public Task<Page<Resume>> GetPageByOwnerIdAsync(
        AccountId ownerId, PageRequest page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_resumes.Values
            .Where(row => row.Item.OwnerId.Value == ownerId.Value)
            .ToNewestFirstPage(page));
    }

    public Task AddAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes[resume.Id.Value] = NextRow(resume);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes.AddOrUpdate(
            resume.Id.Value,
            _ => NextRow(resume),
            (_, existing) => existing with { Item = resume });
        return Task.CompletedTask;
    }

    // MIRRORS ResumeRepository.CascadeToAnalysesAsync, and the decision it copies is written there:
    // deleting a resume takes the analyses derived from it out of every read, in the same operation,
    // because Analysis holds a ResumeId and no foreign key, so nothing else reaches them.
    //
    // Dropping them rather than tombstoning them is the only shape available in this store — Analysis
    // has no Status and IAnalysisRepository has no delete, so there is no state a filter could read (see
    // InMemoryAnalysisRepository). After the query filter has run, the two providers answer the same.
    //
    // WITHOUT THIS the divergence was not merely a paging detail (issue #18). The whole Api suite runs
    // on this store, so a handler that read an analysis WITHOUT loading its resume first would have been
    // certified green here against behaviour SQL Server does not have — and it would have been certified
    // green on a privacy promise, since "delete my resume" is documented to take everything derived from
    // it. Both endpoints that read an analysis happen to load the resume first today, which is the only
    // reason nothing observed it.
    public Task DeleteAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes.TryRemove(id.Value, out _);
        _itemIds.TryRemove(id.Value, out _);
        analyses.RemoveAllDerivedFrom(id);
        return Task.CompletedTask;
    }

    // Seeded at 1, matching IDENTITY(1,1) — and matching Cursor, which refuses a position of zero
    // precisely so "no cursor" can never be confused with "positioned at the first row".
    private KeysetRow<Resume> NextRow(Resume resume) => new(resume, Interlocked.Increment(ref _sequence));

    /// <summary>
    /// Hands each collection entry of one resume a number and remembers it for as long as that entry
    /// stays on the aggregate.
    /// </summary>
    /// <remarks>
    /// Seeded at 1 like every other surrogate in this store, so a zero can never be mistaken for an id.
    /// Numbers are never reused: an entry that is removed takes its id out of circulation, which is what
    /// stops a stale client from deleting whatever landed in the position its id used to occupy.
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

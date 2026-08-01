using System.Collections.Concurrent;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
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
public sealed class InMemoryResumeRepository : IResumeRepository
{
    private readonly ConcurrentDictionary<Guid, KeysetRow<Resume>> _resumes = new();
    private long _sequence;

    public Task<Resume?> GetByIdAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes.TryGetValue(id.Value, out var row);
        return Task.FromResult(row?.Item);
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

    public Task DeleteAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes.TryRemove(id.Value, out _);
        return Task.CompletedTask;
    }

    // Seeded at 1, matching IDENTITY(1,1) — and matching Cursor, which refuses a position of zero
    // precisely so "no cursor" can never be confused with "positioned at the first row".
    private KeysetRow<Resume> NextRow(Resume resume) => new(resume, Interlocked.Increment(ref _sequence));
}

using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

namespace BuildCv.Infrastructure.Persistence;

public sealed class InMemoryResumeRepository : IResumeRepository
{
    private readonly ConcurrentDictionary<Guid, Resume> _resumes = new();

    public Task<Resume?> GetByIdAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes.TryGetValue(id.Value, out var resume);
        return Task.FromResult(resume);
    }

    public Task<IReadOnlyList<Resume>> GetByOwnerIdAsync(AccountId ownerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Resume> resumes = _resumes.Values
            .Where(r => r.OwnerId.Value == ownerId.Value)
            .ToList();
        return Task.FromResult(resumes);
    }

    public Task AddAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes[resume.Id.Value] = resume;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes[resume.Id.Value] = resume;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resumes.TryRemove(id.Value, out _);
        return Task.CompletedTask;
    }
}

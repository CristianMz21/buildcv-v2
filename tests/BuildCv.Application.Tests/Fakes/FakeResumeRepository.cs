namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed class FakeResumeRepository : IResumeRepository
{
    private readonly List<Resume> _resumes = [];

    public Task<Resume?> GetByIdAsync(ResumeId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_resumes.FirstOrDefault(r => r.Id == id));

    public Task<IReadOnlyList<Resume>> GetByOwnerIdAsync(AccountId ownerId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Resume>>(_resumes.Where(r => r.OwnerId == ownerId).ToList());

    public Task AddAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        _resumes.Add(resume);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        var index = _resumes.FindIndex(r => r.Id == resume.Id);
        if (index >= 0)
            _resumes[index] = resume;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        _resumes.RemoveAll(r => r.Id == id);
        return Task.CompletedTask;
    }
}

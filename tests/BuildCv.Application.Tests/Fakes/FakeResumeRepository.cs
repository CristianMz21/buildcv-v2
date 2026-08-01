namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

// Carries an insertion counter for the same reason the in-memory store does: it stands in for the
// bigint IDENTITY Seq column, so a handler test walks pages in the order and at the boundaries SQL
// Server produces rather than in list order. Assigned on Add, never moved by Update.
public sealed class FakeResumeRepository : IResumeRepository
{
    private readonly List<KeysetRow<Resume>> _resumes = [];
    private long _sequence;

    public Task<Resume?> GetByIdAsync(ResumeId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_resumes.FirstOrDefault(row => row.Item.Id == id)?.Item);

    public Task<Page<Resume>> GetPageByOwnerIdAsync(
        AccountId ownerId, PageRequest page, CancellationToken cancellationToken = default) =>
        Task.FromResult(_resumes.Where(row => row.Item.OwnerId == ownerId).ToNewestFirstPage(page));

    public Task AddAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        _resumes.Add(new KeysetRow<Resume>(resume, ++_sequence));
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        var index = _resumes.FindIndex(row => row.Item.Id == resume.Id);
        if (index >= 0)
            _resumes[index] = _resumes[index] with { Item = resume };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        _resumes.RemoveAll(row => row.Item.Id == id);
        return Task.CompletedTask;
    }
}

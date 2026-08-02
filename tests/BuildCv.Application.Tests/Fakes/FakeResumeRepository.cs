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

    // Counts the calls that would have been a database round trip.
    //
    // It exists for one assertion: that a malformed cursor is rejected BEFORE anything queries the
    // store. Without the counter that guarantee is untestable — move the validation after the
    // repository call and every other assertion in the invalid-cursor test stays green, so the test
    // name would be the only thing claiming it, and names are what get trusted during a refactor.
    public int ReadCount { get; private set; }

    // Counts inserts. A rejected draft must not reach the store at all, and "the result says failure"
    // and "nothing was created" are different claims — only this one is about the store.
    public int AddCount { get; private set; }

    // Counts EVERY write, insert and update alike, because AddCount alone cannot see the regression it
    // matters most against: a draft import assembled section by section would be one AddAsync followed
    // by ten UpdateAsync calls, leaving identical contents behind and an AddCount of exactly 1.
    public int WriteCount { get; private set; }

    public Task<Resume?> GetByIdAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_resumes.FirstOrDefault(row => row.Item.Id == id)?.Item);
    }

    public Task<Page<Resume>> GetPageByOwnerIdAsync(
        AccountId ownerId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_resumes.Where(row => row.Item.OwnerId == ownerId).ToNewestFirstPage(page));
    }

    public Task AddAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        AddCount++;
        WriteCount++;
        _resumes.Add(new KeysetRow<Resume>(resume, ++_sequence));
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        WriteCount++;
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

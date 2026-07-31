namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public interface IResumeRepository
{
    Task<Resume?> GetByIdAsync(ResumeId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Resume>> GetByOwnerIdAsync(AccountId ownerId, CancellationToken cancellationToken = default);
    Task AddAsync(Resume resume, CancellationToken cancellationToken = default);
    Task UpdateAsync(Resume resume, CancellationToken cancellationToken = default);
    Task DeleteAsync(ResumeId id, CancellationToken cancellationToken = default);
}

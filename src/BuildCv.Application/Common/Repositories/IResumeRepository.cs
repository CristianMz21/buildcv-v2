namespace BuildCv.Application.Common.Repositories;

using BuildCv.Application.Common.Pagination;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public interface IResumeRepository
{
    Task<Resume?> GetByIdAsync(ResumeId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same resume, plus the identity of every entry in its ten collections.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="GetByIdAsync"/> ON PURPOSE, and not merged into it. The ids are only
    /// obtainable from a tracked materialization, and most callers — scoring, readability, every
    /// section append — neither address an entry nor should pay for tracking to avoid a second method.
    /// This one is for the two operations that must name one entry out of many: removing and replacing.
    /// </remarks>
    Task<ResumeWithItemIds?> GetByIdWithItemIdsAsync(
        ResumeId id, CancellationToken cancellationToken = default);

    // Newest first, one page at a time. There is deliberately no unbounded overload: an owner's resume
    // count is user-controlled, so "list them all" is a query whose cost the caller cannot bound.
    Task<Page<Resume>> GetPageByOwnerIdAsync(AccountId ownerId, PageRequest page, CancellationToken cancellationToken = default);
    Task AddAsync(Resume resume, CancellationToken cancellationToken = default);
    Task UpdateAsync(Resume resume, CancellationToken cancellationToken = default);
    Task DeleteAsync(ResumeId id, CancellationToken cancellationToken = default);
}

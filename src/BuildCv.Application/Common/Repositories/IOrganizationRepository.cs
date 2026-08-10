namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

public interface IOrganizationRepository
{
    Task<Organization?> GetByIdAsync(OrganizationId id, CancellationToken cancellationToken = default);
    Task<Organization?> GetBySlugAsync(Slug slug, CancellationToken cancellationToken = default);
    Task AddAsync(Organization organization, CancellationToken cancellationToken = default);
    Task UpdateAsync(Organization organization, CancellationToken cancellationToken = default);

    // Every organization this account belongs to. Unpaged, and it is the one place in this codebase that
    // is allowed to be: the list-query rule forbids unbounded reads because a list GROWS -- an account's
    // resumes, an organization's postings. Memberships do not. An account joins organizations one
    // deliberate invitation at a time, and this is read exactly once, by account deletion, which must see
    // ALL of them or leave a dangling membership behind. A page would answer "the first twenty" to a
    // question whose only useful answer is "every one".
    Task<IReadOnlyList<Organization>> GetByMemberIdAsync(
        AccountId accountId, CancellationToken cancellationToken = default);
}

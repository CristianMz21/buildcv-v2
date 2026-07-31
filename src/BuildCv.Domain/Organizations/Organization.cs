using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

namespace BuildCv.Domain.Organizations;

using BuildCv.Domain.Common.ValueObjects;

public sealed class Organization
{
    public OrganizationId Id { get; }
    public OrganizationName Name { get; }
    public Slug Slug { get; }
    public OrganizationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<Membership> Members { get; private set; }

    private Organization(OrganizationId id, OrganizationName name, Slug slug, Membership founder)
    {
        var now = DateTimeOffset.UtcNow;
        Id = id;
        Name = name;
        Slug = slug;
        Status = OrganizationStatus.Active;
        CreatedAt = now;
        UpdatedAt = now;
        Members = new List<Membership> { founder }.AsReadOnly();
    }

    public static Organization Create(OrganizationName name, Slug slug, AccountId founderId)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(founderId);

        var founder = new Membership(founderId, MembershipRole.Owner, DateTimeOffset.UtcNow);
        return new Organization(OrganizationId.New(), name, slug, founder);
    }

    public void AddMember(AccountId accountId, MembershipRole role = MembershipRole.Member)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        if (Members.Any(m => m.AccountId == accountId))
            throw new InvalidMembershipException("Account is already a member.");

        var updated = new List<Membership>(Members)
        {
            new(accountId, role, DateTimeOffset.UtcNow)
        };
        Members = updated.AsReadOnly();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RemoveMember(AccountId accountId)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        var member = Members.FirstOrDefault(m => m.AccountId == accountId)
            ?? throw new InvalidMembershipException("Account is not a member.");

        if (member.Role == MembershipRole.Owner && Members.Count(m => m.Role == MembershipRole.Owner) == 1)
            throw new InvalidMembershipException("Cannot remove the only owner of an organization.");

        Members = Members.Where(m => m.AccountId != accountId).ToList().AsReadOnly();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ChangeMemberRole(AccountId accountId, MembershipRole newRole)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        var member = Members.FirstOrDefault(m => m.AccountId == accountId)
            ?? throw new InvalidMembershipException("Account is not a member.");

        if (member.Role == MembershipRole.Owner && newRole != MembershipRole.Owner
            && Members.Count(m => m.Role == MembershipRole.Owner) == 1)
            throw new InvalidMembershipException("Cannot demote the only owner of an organization.");

        var updated = new List<Membership>();
        foreach (var m in Members)
        {
            updated.Add(m.AccountId == accountId
                ? m with { Role = newRole }
                : m);
        }
        Members = updated.AsReadOnly();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Suspend()
    {
        Status = OrganizationStatus.Suspended;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Restore()
    {
        Status = OrganizationStatus.Active;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Delete()
    {
        Status = OrganizationStatus.Deleted;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public override bool Equals(object? obj) => obj is Organization other && Id.Equals(other.Id);
    public override int GetHashCode() => Id.GetHashCode();
}

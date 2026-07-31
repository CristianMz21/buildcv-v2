using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

namespace BuildCv.Domain.Organizations;

using BuildCv.Domain.Common.ValueObjects;

public sealed class Organization
{
    private readonly List<Membership> _members = [];

    public OrganizationId Id { get; }
    public OrganizationName Name { get; }
    public Slug Slug { get; }
    public OrganizationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<Membership> Members => _members;

    private Organization(OrganizationId id, OrganizationName name, Slug slug)
    {
        var now = DateTimeOffset.UtcNow;
        Id = id;
        Name = name;
        Slug = slug;
        Status = OrganizationStatus.Active;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static Organization Create(OrganizationName name, Slug slug, AccountId founderId)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(founderId);

        var organization = new Organization(OrganizationId.New(), name, slug);
        organization._members.Add(new Membership(founderId, MembershipRole.Owner, DateTimeOffset.UtcNow));
        return organization;
    }

    public void AddMember(AccountId accountId, MembershipRole role = MembershipRole.Member)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        if (Members.Any(m => m.AccountId == accountId))
            throw new InvalidMembershipException("Account is already a member.");

        _members.Add(new Membership(accountId, role, DateTimeOffset.UtcNow));
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RemoveMember(AccountId accountId)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        var member = Members.FirstOrDefault(m => m.AccountId == accountId)
            ?? throw new InvalidMembershipException("Account is not a member.");

        if (member.Role == MembershipRole.Owner && Members.Count(m => m.Role == MembershipRole.Owner) == 1)
            throw new InvalidMembershipException("Cannot remove the only owner of an organization.");

        _members.Remove(member);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ChangeMemberRole(AccountId accountId, MembershipRole newRole)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        var index = _members.FindIndex(m => m.AccountId == accountId);
        if (index < 0)
            throw new InvalidMembershipException("Account is not a member.");
        var member = _members[index];

        if (member.Role == MembershipRole.Owner && newRole != MembershipRole.Owner
            && Members.Count(m => m.Role == MembershipRole.Owner) == 1)
            throw new InvalidMembershipException("Cannot demote the only owner of an organization.");

        _members[index] = member with { Role = newRole };
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

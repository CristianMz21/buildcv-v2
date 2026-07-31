using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

namespace BuildCv.Domain.Organizations;

public sealed class Organization
{
    public OrganizationId Id { get; }
    public OrganizationName Name { get; }
    public Slug Slug { get; }
    public OrganizationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<Membership> Members { get; private set; }

    private Organization(OrganizationId id, OrganizationName name, Slug slug, Membership founder)
    {
        Id = id;
        Name = name;
        Slug = slug;
        Status = OrganizationStatus.Active;
        CreatedAt = DateTimeOffset.UtcNow;
        Members = new List<Membership> { founder }.AsReadOnly();
    }

    public static Organization Create(OrganizationName name, Slug slug, AccountId founderId)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(slug);

        var founder = new Membership(founderId, MembershipRole.Owner, DateTimeOffset.UtcNow);
        return new Organization(OrganizationId.New(), name, slug, founder);
    }

    public void AddMember(AccountId accountId, MembershipRole role = MembershipRole.Member)
    {
        if (Members.Any(m => m.AccountId == accountId))
            throw new InvalidMembershipException($"Account {accountId.Value} is already a member.");

        var updated = new List<Membership>(Members)
        {
            new(accountId, role, DateTimeOffset.UtcNow)
        };
        Members = updated.AsReadOnly();
    }

    public void RemoveMember(AccountId accountId)
    {
        var member = Members.FirstOrDefault(m => m.AccountId == accountId)
            ?? throw new InvalidMembershipException($"Account {accountId.Value} is not a member.");

        if (member.Role == MembershipRole.Owner && Members.Count(m => m.Role == MembershipRole.Owner) == 1)
            throw new InvalidOperationException("Cannot remove the only owner of an organization.");

        Members = Members.Where(m => m.AccountId != accountId).ToList().AsReadOnly();
    }

    public void ChangeMemberRole(AccountId accountId, MembershipRole newRole)
    {
        var member = Members.FirstOrDefault(m => m.AccountId == accountId)
            ?? throw new InvalidMembershipException($"Account {accountId.Value} is not a member.");

        if (member.Role == MembershipRole.Owner && newRole != MembershipRole.Owner
            && Members.Count(m => m.Role == MembershipRole.Owner) == 1)
            throw new InvalidOperationException("Cannot demote the only owner of an organization.");

        var updated = new List<Membership>();
        foreach (var m in Members)
        {
            updated.Add(m.AccountId == accountId
                ? m with { Role = newRole }
                : m);
        }
        Members = updated.AsReadOnly();
    }

    public void Suspend() => Status = OrganizationStatus.Suspended;
    public void Restore() => Status = OrganizationStatus.Active;
}

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Organizations;

public class OrganizationTests
{
    private static Organization Build() => Organization.Create(
        OrganizationName.Create("TechCorp"),
        Slug.Create("techcorp"),
        AccountId.New());

    [Fact]
    public void Organization_create_adds_founder_as_owner()
    {
        var founderId = AccountId.New();
        var org = Organization.Create(
            OrganizationName.Create("TechCorp"),
            Slug.Create("techcorp"),
            founderId);

        org.Members.Should().HaveCount(1);
        org.Members[0].AccountId.Should().Be(founderId);
        org.Members[0].Role.Should().Be(MembershipRole.Owner);
        org.Status.Should().Be(OrganizationStatus.Active);
        org.UpdatedAt.Should().Be(org.CreatedAt);
    }

    [Fact]
    public void Organization_null_founder_throws()
    {
        var act = () => Organization.Create(
            OrganizationName.Create("TechCorp"),
            Slug.Create("techcorp"),
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Organization_cannot_remove_only_owner()
    {
        var founderId = AccountId.New();
        var org = Organization.Create(
            OrganizationName.Create("TechCorp"),
            Slug.Create("techcorp"),
            founderId);

        var act = () => org.RemoveMember(founderId);

        act.Should().Throw<InvalidMembershipException>();
    }

    [Fact]
    public void Organization_cannot_demote_only_owner()
    {
        var founderId = AccountId.New();
        var org = Organization.Create(
            OrganizationName.Create("TechCorp"),
            Slug.Create("techcorp"),
            founderId);

        var act = () => org.ChangeMemberRole(founderId, MembershipRole.Member);

        act.Should().Throw<InvalidMembershipException>();
    }

    [Fact]
    public void Organization_add_member_then_remove()
    {
        var org = Build();
        var member = AccountId.New();

        org.AddMember(member);
        org.Members.Should().HaveCount(2);

        org.RemoveMember(member);
        org.Members.Should().ContainSingle();
    }

    [Fact]
    public void Organization_add_duplicate_member_throws()
    {
        var org = Build();
        var member = AccountId.New();
        org.AddMember(member);

        var act = () => org.AddMember(member);

        act.Should().Throw<InvalidMembershipException>();
    }

    [Fact]
    public void Organization_delete_sets_deleted_status()
    {
        var org = Build();

        org.Delete();

        org.Status.Should().Be(OrganizationStatus.Deleted);
    }

    [Fact]
    public void Organization_suspend_then_restore()
    {
        var org = Build();

        org.Suspend();
        org.Status.Should().Be(OrganizationStatus.Suspended);

        org.Restore();
        org.Status.Should().Be(OrganizationStatus.Active);
    }

    [Fact]
    public void Organization_entity_equality_by_id()
    {
        var org = Build();

        org.Equals(org).Should().BeTrue();
        Build().Equals(org).Should().BeFalse();
    }

    [Fact]
    public void Organization_members_view_reflects_subsequent_additions()
    {
        var org = Build();
        var view = org.Members;

        org.AddMember(AccountId.New());

        view.Should().HaveCount(2);
    }
}

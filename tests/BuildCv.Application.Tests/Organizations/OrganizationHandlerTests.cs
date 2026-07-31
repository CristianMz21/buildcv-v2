using BuildCv.Application.Organizations;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;
using FluentAssertions;

namespace BuildCv.Application.Tests.Organizations;

public class OrganizationHandlerTests
{
    private readonly FakeOrganizationRepository _organizations = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly CreateOrganizationHandler _createHandler;
    private readonly AddMemberHandler _addMemberHandler;

    public OrganizationHandlerTests()
    {
        _createHandler = new CreateOrganizationHandler(_organizations, _accounts);
        _addMemberHandler = new AddMemberHandler(_organizations, _accounts);
    }

    private async Task<Account> SeedAccountAsync(string email)
    {
        var account = Account.Create(
            Email.Create(email),
            Password.Create("$argon2id$v=19$m=65536,t=3,p=1$saltsalt$somehashoutputbyteslong"));
        await _accounts.AddAsync(account);
        return account;
    }

    [Fact]
    public async Task Create_success_makes_founder_owner()
    {
        var founder = await SeedAccountAsync("founder@example.com");

        var result = await _createHandler.Handle(new CreateOrganizationCommand(founder.Id, "Acme Inc", "acme-inc"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Members.Should().HaveCount(1);
        result.Value.Members[0].AccountId.Should().Be(founder.Id);
        result.Value.Members[0].Role.Should().Be(MembershipRole.Owner);
        result.Value.Slug.Value.Should().Be("acme-inc");
    }

    [Fact]
    public async Task Create_duplicate_slug_fails()
    {
        var founder = await SeedAccountAsync("founder@example.com");
        await _createHandler.Handle(new CreateOrganizationCommand(founder.Id, "Acme Inc", "acme-inc"));

        var result = await _createHandler.Handle(new CreateOrganizationCommand(founder.Id, "Acme Clone", "acme-inc"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Slug is already taken.");
    }

    [Fact]
    public async Task Add_member_by_owner_succeeds()
    {
        var founder = await SeedAccountAsync("founder@example.com");
        var member = await SeedAccountAsync("member@example.com");
        var organization = (await _createHandler.Handle(
            new CreateOrganizationCommand(founder.Id, "Acme Inc", "acme-inc"))).Value!;

        var result = await _addMemberHandler.Handle(
            new AddMemberCommand(founder.Id, organization.Id, member.Id, MembershipRole.Member));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Members.Should().HaveCount(2);
        result.Value.Members.Any(m => m.AccountId == member.Id && m.Role == MembershipRole.Member).Should().BeTrue();
    }

    [Fact]
    public async Task Add_member_forbidden_for_plain_member()
    {
        var founder = await SeedAccountAsync("founder@example.com");
        var member = await SeedAccountAsync("member@example.com");
        var outsider = await SeedAccountAsync("outsider@example.com");
        var organization = (await _createHandler.Handle(
            new CreateOrganizationCommand(founder.Id, "Acme Inc", "acme-inc"))).Value!;
        await _addMemberHandler.Handle(new AddMemberCommand(founder.Id, organization.Id, member.Id, MembershipRole.Member));

        var result = await _addMemberHandler.Handle(
            new AddMemberCommand(member.Id, organization.Id, outsider.Id, MembershipRole.Member));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
    }

    [Fact]
    public async Task Add_member_unknown_account_fails()
    {
        var founder = await SeedAccountAsync("founder@example.com");
        var organization = (await _createHandler.Handle(
            new CreateOrganizationCommand(founder.Id, "Acme Inc", "acme-inc"))).Value!;

        var result = await _addMemberHandler.Handle(
            new AddMemberCommand(founder.Id, organization.Id, AccountId.New(), MembershipRole.Member));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Member account not found or not active.");
    }
}

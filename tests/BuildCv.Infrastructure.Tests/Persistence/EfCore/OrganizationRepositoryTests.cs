using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;
using BuildCv.Infrastructure.Persistence.Conventions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OrganizationRepositoryTests
{
    private readonly SqlServerFixture _fixture;

    public OrganizationRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AddAsync_ThenGetByIdAsync_RoundTripsWithItsMemberships()
    {
        var founder = AccountId.New();
        var organization = NewOrganization(founder);
        organization.AddMember(AccountId.New(), MembershipRole.Admin);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Organizations(writer).AddAsync(organization);

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Organizations(reader).GetByIdAsync(organization.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Name.Should().Be(organization.Name);
        reloaded.Members.Should().HaveCount(2);
        reloaded.Members.Should().Contain(member => member.AccountId == founder);
    }

    // Slug is analytical and plaintext, so unlike Account.Email it is matched directly in SQL. This is
    // the test that would fail the day somebody decided to encrypt it.
    [Fact]
    public async Task GetBySlugAsync_FindsTheOrganizationByItsPublicHandle()
    {
        var organization = NewOrganization(AccountId.New());

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Organizations(writer).AddAsync(organization);

        await using var reader = _fixture.NewApplicationContext();
        var repository = TestRepositories.Organizations(reader);

        (await repository.GetBySlugAsync(organization.Slug))!.Id.Should().Be(organization.Id);
        (await repository.GetBySlugAsync(Slug.Create($"absent-{Guid.NewGuid():N}"))).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_PersistsAMembershipChange()
    {
        var organization = NewOrganization(AccountId.New());
        var newcomer = AccountId.New();

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Organizations(writer).AddAsync(organization);

        await using (var mutator = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Organizations(mutator);
            var loaded = await repository.GetBySlugAsync(organization.Slug);
            loaded!.AddMember(newcomer, MembershipRole.Member);
            await repository.UpdateAsync(loaded);
        }

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Organizations(reader).GetByIdAsync(organization.Id);

        reloaded!.Members.Should().Contain(member => member.AccountId == newcomer);
    }

    // The same reconciliation as Account, on the other aggregate that exposes a domain-level Delete().
    // The filtered unique index on Slug promises a deleted organization releases its public handle, and
    // that promise is only kept if the domain delete reaches DeletedAt.
    [Fact]
    public async Task UpdateAsync_AfterADomainDelete_TombstonesTheRowAndReleasesTheSlug()
    {
        var principal = AccountId.New();
        var organization = NewOrganization(AccountId.New());
        var slug = organization.Slug;

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Organizations(writer).AddAsync(organization);

        await using (var deleter = _fixture.NewApplicationContext(new StubCurrentUser(principal)))
        {
            var repository = TestRepositories.Organizations(deleter);
            var loaded = await repository.GetByIdAsync(organization.Id);
            loaded!.Delete();
            await repository.UpdateAsync(loaded);
        }

        await using var reader = _fixture.NewApplicationContext();

        (await TestRepositories.Organizations(reader).GetByIdAsync(organization.Id)).Should().BeNull();
        (await TestRepositories.Organizations(reader).GetBySlugAsync(slug)).Should().BeNull();

        var tombstoned = await reader.Organizations.AsTracking()
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == organization.Id);

        tombstoned.Status.Should().Be(OrganizationStatus.Deleted);
        tombstoned.Members.Should().ContainSingle("a tombstone keeps the aggregate whole");

        var entry = reader.Entry(tombstoned);
        entry.Property(ShadowColumns.DeletedAt).CurrentValue.Should().NotBeNull();
        entry.Property(ShadowColumns.DeletedBy).CurrentValue.Should().Be(principal.Value);

        // The handle is genuinely free again, not merely hidden.
        var replacement = Organization.Create(OrganizationName.Create("Successor"), slug, AccountId.New());

        await using var reregister = _fixture.NewApplicationContext();
        var act = async () => await TestRepositories.Organizations(reregister).AddAsync(replacement);

        await act.Should().NotThrowAsync();
    }

    private static Organization NewOrganization(AccountId founder) =>
        Organization.Create(
            OrganizationName.Create("Contoso"), Slug.Create($"contoso-{Guid.NewGuid():N}"), founder);
}

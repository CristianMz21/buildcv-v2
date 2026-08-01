using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class JobPostingRepositoryTests
{
    private readonly SqlServerFixture _fixture;

    public JobPostingRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AddAsync_ThenGetByIdAsync_RoundTripsWithItsOwnedCollections()
    {
        var posting = NewPosting(AccountId.New());
        posting.SetRequirements(
        [
            JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave, 3),
            JobRequirement.Create(Technology.Create("SQL"), RequirementPriority.NiceToHave, 1),
        ]);
        posting.SetResponsibilities([Responsibility.Create("Ship features.")]);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.JobPostings(writer).AddAsync(posting);

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.JobPostings(reader).GetByIdAsync(posting.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Title.Should().Be(posting.Title);
        reloaded.Requirements.Should().BeEquivalentTo(posting.Requirements);
        reloaded.Responsibilities.Should().BeEquivalentTo(posting.Responsibilities);
    }

    [Fact]
    public async Task GetPageByOwnerIdAsync_ReturnsOnlyThatOwnersPostings_NewestFirst()
    {
        var owner = AccountId.New();
        var first = NewPosting(owner);
        var second = NewPosting(owner);

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.JobPostings(writer);
            await repository.AddAsync(first);
            await repository.AddAsync(second);
            await repository.AddAsync(NewPosting(AccountId.New()));
        }

        await using var reader = _fixture.NewApplicationContext();
        var mine = await TestRepositories.JobPostings(reader).GetPageByOwnerIdAsync(owner, PageRequests.Of());

        mine.Items.Select(posting => posting.Id).Should().Equal(second.Id, first.Id);
        mine.NextCursor.Should().BeNull();
    }

    // CompanyId is nullable, so this query also has to leave the postings that belong to no organization
    // out rather than sweeping up every NULL.
    [Fact]
    public async Task GetPageByOrganizationIdAsync_ReturnsOnlyThePostingsOfThatOrganization()
    {
        var organizationId = OrganizationId.New();
        var owned = NewPosting(AccountId.New(), organizationId);

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.JobPostings(writer);
            await repository.AddAsync(owned);
            await repository.AddAsync(NewPosting(AccountId.New(), OrganizationId.New()));
            await repository.AddAsync(NewPosting(AccountId.New()));
        }

        await using var reader = _fixture.NewApplicationContext();
        var found = await TestRepositories.JobPostings(reader)
            .GetPageByOrganizationIdAsync(organizationId, PageRequests.Of());

        found.Items.Should().ContainSingle().Which.Id.Should().Be(owned.Id);
    }

    [Fact]
    public async Task UpdateAsync_PersistsALifecycleTransition()
    {
        var posting = NewPosting(AccountId.New());
        posting.SetRequirements([JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave, 2)]);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.JobPostings(writer).AddAsync(posting);

        await using (var mutator = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.JobPostings(mutator);
            var loaded = await repository.GetByIdAsync(posting.Id);
            loaded!.Publish();
            await repository.UpdateAsync(loaded);
        }

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.JobPostings(reader).GetByIdAsync(posting.Id);

        reloaded!.Status.Should().Be(JobPostingStatus.Published);
    }

    // The two factories are the Domain's way of saying a posting belongs EITHER to a named employer or
    // to an organization in this system, never to both.
    private static JobPosting NewPosting(AccountId ownerId, OrganizationId? companyId = null) =>
        companyId is null
            ? JobPosting.Create(
                ownerId, $"Senior .NET Engineer {Guid.NewGuid():N}", OrganizationName.Create("Contoso"), "Build things.")
            : JobPosting.CreateForOrganization(
                ownerId, companyId, $"Senior .NET Engineer {Guid.NewGuid():N}", "Build things.");
}

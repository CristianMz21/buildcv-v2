using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Security;
using BuildCv.Infrastructure.Security.Encryption;
using BuildCv.Infrastructure.Tests.Security.Encryption;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AccountRepositoryTests
{
    private readonly SqlServerFixture _fixture;

    public AccountRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AddAsync_ThenGetByIdAsync_RoundTripsThroughAFreshContext()
    {
        var account = NewAccount(UniqueEmail("byid"));

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Accounts(writer).AddAsync(account);

        await using var reader = _fixture.NewApplicationContext();
        var found = await TestRepositories.Accounts(reader).GetByIdAsync(account.Id);

        found.Should().NotBeNull();
        found!.Email.Value.Should().Be(account.Email.Value);
        found.Password.Hash.Should().Be(account.Password.Hash);
        found.Role.Should().Be(account.Role);
    }

    // The chain the product depends on, end to end: the Domain lower-cases in Email.Create, the blind
    // index hashes what the Domain produced, and the lookup therefore matches whatever casing the caller
    // typed. Break any link and registering "USER@Example.com" writes a digest that logging in as
    // "user@example.com" never finds — which surfaces as "no such account", not as a bug.
    [Fact]
    public async Task GetByEmailAsync_MatchesRegardlessOfTheCasingTheCallerTyped()
    {
        var address = UniqueEmail("MixedCase");
        var account = NewAccount(address);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Accounts(writer).AddAsync(account);

        await using var reader = _fixture.NewApplicationContext();
        var repository = TestRepositories.Accounts(reader);

        (await repository.GetByEmailAsync(Email.Create(address))).Should().NotBeNull();
        (await repository.GetByEmailAsync(Email.Create(address.ToUpperInvariant()))).Should().NotBeNull();
        (await repository.GetByEmailAsync(Email.Create(address.ToLowerInvariant()))).Should().NotBeNull();
        (await repository.ExistsByEmailAsync(Email.Create(address.ToUpperInvariant()))).Should().BeTrue();
    }

    [Fact]
    public async Task ExistsByEmailAsync_ForAnUnregisteredAddress_IsFalse()
    {
        await using var reader = _fixture.NewApplicationContext();

        (await TestRepositories.Accounts(reader).ExistsByEmailAsync(Email.Create(UniqueEmail("absent"))))
            .Should().BeFalse();
    }

    // The race registration cannot check its way out of: two requests both pass ExistsByEmailAsync before
    // either commits, and only the unique index on EmailHash stops the second. It has to arrive as
    // something the Api can turn into a 409, not as a raw vendor error number.
    [Fact]
    public async Task AddAsync_WithAnAddressAlreadyTaken_ThrowsDuplicateKeyException()
    {
        var address = UniqueEmail("duplicate");

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Accounts(writer).AddAsync(NewAccount(address));

        await using var second = _fixture.NewApplicationContext();

        var act = async () => await TestRepositories.Accounts(second).AddAsync(NewAccount(address));

        await act.Should().ThrowAsync<DuplicateKeyException>();
    }

    [Fact]
    public async Task UpdateAsync_PersistsAMutationMadeOnTheTrackedAggregate()
    {
        var account = NewAccount(UniqueEmail("update"));

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Accounts(writer).AddAsync(account);

        await using (var mutator = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Accounts(mutator);
            var loaded = await repository.GetByIdAsync(account.Id);
            loaded!.RecordFailedLogin();
            await repository.UpdateAsync(loaded);
        }

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Accounts(reader).GetByIdAsync(account.Id);

        reloaded!.FailedLoginCount.Should().Be(1);
    }

    // The rowversion, surfaced through the port. Without the translation this arrives as a
    // DbUpdateConcurrencyException, which forces every caller that wants to react to it to reference EF.
    [Fact]
    public async Task UpdateAsync_WhenTheRowMovedUnderIt_ThrowsConcurrencyConflictException()
    {
        var account = NewAccount(UniqueEmail("concurrency"));

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Accounts(writer).AddAsync(account);

        await using var first = _fixture.NewApplicationContext();
        await using var second = _fixture.NewApplicationContext();

        var firstRepository = TestRepositories.Accounts(first);
        var secondRepository = TestRepositories.Accounts(second);

        var firstCopy = await firstRepository.GetByIdAsync(account.Id);
        var secondCopy = await secondRepository.GetByIdAsync(account.Id);

        firstCopy!.ChangeRole(Role.Recruiter);
        await firstRepository.UpdateAsync(firstCopy);

        secondCopy!.ChangeRole(Role.Admin);
        var act = async () => await secondRepository.UpdateAsync(secondCopy);

        await act.Should().ThrowAsync<ConcurrencyConflictException>();
    }

    // THE soft-delete reconciliation. Account.Delete() only sets Status; the repository writes the
    // tombstone alongside it, in one UPDATE, so the row is never observable in a half-deleted shape.
    [Fact]
    public async Task UpdateAsync_AfterADomainDelete_TombstonesTheRowAndMarksBothRepresentations()
    {
        var principal = AccountId.New();
        var account = NewAccount(UniqueEmail("tombstone"));

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Accounts(writer).AddAsync(account);

        await using (var deleter = _fixture.NewApplicationContext(new StubCurrentUser(principal)))
        {
            var repository = TestRepositories.Accounts(deleter);
            var loaded = await repository.GetByIdAsync(account.Id);
            loaded!.Delete();
            await repository.UpdateAsync(loaded);
        }

        await using var reader = _fixture.NewApplicationContext();

        // Invisible to a normal query, which is what "deleted" has to mean to the rest of the system.
        (await TestRepositories.Accounts(reader).GetByIdAsync(account.Id)).Should().BeNull();
        (await TestRepositories.Accounts(reader).GetByEmailAsync(account.Email)).Should().BeNull();
        (await TestRepositories.Accounts(reader).ExistsByEmailAsync(account.Email)).Should().BeFalse();

        // Still on disk, carrying BOTH marks. The status is the half the audit interceptor would have
        // discarded had this gone through Remove().
        var tombstoned = await reader.Accounts.AsTracking()
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == account.Id);

        tombstoned.Status.Should().Be(AccountStatus.Deleted, "the domain half of the delete must survive");

        var entry = reader.Entry(tombstoned);
        entry.Property(ShadowColumns.DeletedAt).CurrentValue.Should().NotBeNull(
            "the persistence half of the delete is what the filtered unique index reads");
        entry.Property(ShadowColumns.DeletedBy).CurrentValue.Should().Be(principal.Value,
            "a tombstone written outside Remove() still has to record who wrote it");
    }

    // The promise the filtered unique index on EmailHash makes in its own comment, checked against the
    // domain delete rather than against context.Remove(). Before the reconciliation this failed: the row
    // stayed live, so the index still held the address and re-registration hit a duplicate forever.
    [Fact]
    public async Task AddAsync_AfterADomainDelete_ReRegistersTheSameAddressAsANewAccount()
    {
        var address = UniqueEmail("freed");
        var original = NewAccount(address);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Accounts(writer).AddAsync(original);

        await using (var deleter = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Accounts(deleter);
            var loaded = await repository.GetByIdAsync(original.Id);
            loaded!.Delete();
            await repository.UpdateAsync(loaded);
        }

        var replacement = NewAccount(address);

        await using (var reregister = _fixture.NewApplicationContext())
            await TestRepositories.Accounts(reregister).AddAsync(replacement);

        await using var reader = _fixture.NewApplicationContext();
        var found = await TestRepositories.Accounts(reader).GetByEmailAsync(Email.Create(address));

        found.Should().NotBeNull();
        found!.Id.Should().Be(replacement.Id, "the live row for this address is the new account");
        found.Id.Should().NotBe(original.Id);

        // Both rows are really there; only one of them is live.
        var rows = await reader.Accounts.IgnoreQueryFilters()
            .Where(entity => entity.Id == original.Id || entity.Id == replacement.Id)
            .CountAsync();
        rows.Should().Be(2, "a tombstone frees the address without destroying the audit record");
    }

    // Requirement 4, reproduced rather than asserted about. The row is written under key b1 alone and
    // read back through a ring whose ACTIVE key is b2 — the exact shape of a rotation window. A lookup
    // built on Compute() sees only the b2 digest, answers "no such account", and registration's
    // duplicate check then lets the same address through a second time.
    [Fact]
    public async Task GetByEmailAsync_DuringAKeyRotation_StillFindsRowsWrittenUnderTheRetiredKey()
    {
        var address = UniqueEmail("rotation");
        var account = NewAccount(address);

        var retiredOnly = new HmacBlindIndex(EncryptionTestKeys.BlindIndexRing("b1", "b1"));
        var rotated = new HmacBlindIndex(EncryptionTestKeys.BlindIndexRing("b2", "b2", "b1"));

        await using (var writer = _fixture.NewApplicationContext(blindIndex: retiredOnly))
            await TestRepositories.Accounts(writer, retiredOnly).AddAsync(account);

        await using var reader = _fixture.NewApplicationContext(blindIndex: rotated);
        var repository = TestRepositories.Accounts(reader, rotated);

        (await repository.GetByEmailAsync(Email.Create(address))).Should().NotBeNull(
            "a lookup must try every configured key, not only the active one");
        (await repository.ExistsByEmailAsync(Email.Create(address))).Should().BeTrue(
            "otherwise the duplicate check passes and the address is registered twice");
    }

    private static Account NewAccount(string email) =>
        Account.Create(Email.Create(email), Password.Create(new PasswordHasher().Hash("correct-horse-battery")));

    private static string UniqueEmail(string label) => $"{label}.{Guid.NewGuid():N}@example.com";
}

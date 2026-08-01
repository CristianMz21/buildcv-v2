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
public sealed class RefreshTokenRepositoryTests
{
    private readonly SqlServerFixture _fixture;

    public RefreshTokenRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AddAsync_ThenGetByTokenAsync_RoundTripsThroughAFreshContext()
    {
        var account = await SeedAccountAsync();
        var token = NewTokenValue();
        var refreshToken = NewRefreshToken(token, account.Id);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.RefreshTokens(writer).AddAsync(refreshToken);

        await using var reader = _fixture.NewApplicationContext();
        var found = await TestRepositories.RefreshTokens(reader).GetByTokenAsync(token);

        found.Should().NotBeNull();
        found!.Token.Should().Be(token);
        found.AccountId.Should().Be(account.Id);
        found.ExpiresAt.Should().BeCloseTo(refreshToken.ExpiresAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task GetByTokenAsync_ForAValueNobodyIssued_ReturnsNull()
    {
        await using var reader = _fixture.NewApplicationContext();
        var repository = TestRepositories.RefreshTokens(reader);

        (await repository.GetByTokenAsync(NewTokenValue())).Should().BeNull();
        (await repository.GetByTokenAsync("   ")).Should().BeNull("a blank cookie is not a lookup");
    }

    // Requirement 5, and the failure it guards against is the worst one on this table: RefreshToken has
    // NO revoked flag, so DeletedAt is the only thing that can make a token stop working. A revocation
    // that does not reach it leaves the row live and the token keeps minting access tokens.
    [Fact]
    public async Task RevokeAsync_TombstonesTheRowRatherThanDeletingIt()
    {
        var principal = AccountId.New();
        var account = await SeedAccountAsync();
        var token = NewTokenValue();

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.RefreshTokens(writer).AddAsync(NewRefreshToken(token, account.Id));

        await using (var revoker = _fixture.NewApplicationContext(new StubCurrentUser(principal)))
            await TestRepositories.RefreshTokens(revoker).RevokeAsync(token);

        await using var reader = _fixture.NewApplicationContext();

        (await TestRepositories.RefreshTokens(reader).GetByTokenAsync(token))
            .Should().BeNull("a revoked token must stop authenticating");

        var tombstoned = await reader.RefreshTokens.AsTracking()
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.AccountId == account.Id);

        var entry = reader.Entry(tombstoned);
        entry.Property(ShadowColumns.DeletedAt).CurrentValue.Should().NotBeNull(
            "the row survives for audit; only the tombstone hides it");
        entry.Property(ShadowColumns.DeletedBy).CurrentValue.Should().Be(principal.Value);
    }

    // The filtered unique index on TokenHash is what makes the tombstone safe to keep: the retired digest
    // stays on disk without holding the column hostage.
    [Fact]
    public async Task RevokeAsync_ThenAddAsync_LetsTheSameAccountHoldANewToken()
    {
        var account = await SeedAccountAsync();
        var first = NewTokenValue();
        var second = NewTokenValue();

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.RefreshTokens(writer).AddAsync(NewRefreshToken(first, account.Id));

        await using (var rotator = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.RefreshTokens(rotator);
            await repository.RevokeAsync(first);
            await repository.AddAsync(NewRefreshToken(second, account.Id));
        }

        await using var reader = _fixture.NewApplicationContext();
        var repositoryReader = TestRepositories.RefreshTokens(reader);

        (await repositoryReader.GetByTokenAsync(first)).Should().BeNull();
        (await repositoryReader.GetByTokenAsync(second)).Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeAsync_ForAValueNobodyIssued_IsANoOp()
    {
        await using var context = _fixture.NewApplicationContext();

        var act = async () => await TestRepositories.RefreshTokens(context).RevokeAsync(NewTokenValue());

        await act.Should().NotThrowAsync();
    }

    // Port semantics: expiry is the HANDLER's decision, not the repository's. RefreshAccessTokenHandler
    // reads IsExpired and answers "Refresh token has expired", which it can only do if the row still
    // comes back. Filtering expired rows out here would collapse that into "Invalid refresh token" and
    // lose the distinction the API reports.
    [Fact]
    public async Task GetByTokenAsync_ForAnExpiredToken_StillReturnsItSoTheHandlerCanRejectIt()
    {
        var account = await SeedAccountAsync();
        var token = NewTokenValue();
        var createdAt = DateTimeOffset.UtcNow.AddDays(-40);

        await using (var writer = _fixture.NewApplicationContext())
        {
            await TestRepositories.RefreshTokens(writer).AddAsync(
                RefreshToken.Create(token, account.Id, createdAt, createdAt.AddDays(30)));
        }

        await using var reader = _fixture.NewApplicationContext();
        var found = await TestRepositories.RefreshTokens(reader).GetByTokenAsync(token);

        found.Should().NotBeNull();
        found!.IsExpired.Should().BeTrue();
    }

    // Same rotation window as AccountRepositoryTests, on the other blind index. A refresh that stops
    // matching mid-rotation logs every active session out at once.
    [Fact]
    public async Task GetByTokenAsync_DuringAKeyRotation_StillFindsTokensWrittenUnderTheRetiredKey()
    {
        var retiredOnly = new HmacBlindIndex(EncryptionTestKeys.BlindIndexRing("b1", "b1"));
        var rotated = new HmacBlindIndex(EncryptionTestKeys.BlindIndexRing("b2", "b2", "b1"));

        var account = await SeedAccountAsync(retiredOnly);
        var token = NewTokenValue();

        await using (var writer = _fixture.NewApplicationContext(blindIndex: retiredOnly))
            await TestRepositories.RefreshTokens(writer, retiredOnly).AddAsync(NewRefreshToken(token, account.Id));

        await using var reader = _fixture.NewApplicationContext(blindIndex: rotated);

        (await TestRepositories.RefreshTokens(reader, rotated).GetByTokenAsync(token)).Should().NotBeNull();
    }

    private async Task<Account> SeedAccountAsync(IBlindIndex? blindIndex = null)
    {
        var account = Account.Create(
            Email.Create($"refresh.{Guid.NewGuid():N}@example.com"),
            Password.Create(new PasswordHasher().Hash("correct-horse-battery")));

        await using var writer = _fixture.NewApplicationContext(blindIndex: blindIndex);
        await TestRepositories.Accounts(writer, blindIndex).AddAsync(account);
        return account;
    }

    // 88 base64 characters, inside the Domain's 43..500 window.
    private static string NewTokenValue() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Convert.ToBase64String(Guid.NewGuid().ToByteArray());

    private static RefreshToken NewRefreshToken(string token, AccountId accountId) =>
        RefreshToken.Create(token, accountId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
}

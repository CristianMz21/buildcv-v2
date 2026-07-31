using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Identity;

public class RefreshTokenTests
{
    private static readonly AccountId AccountId = AccountId.New();
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;

    private static string LongToken(int length) => new('t', length);

    [Fact]
    public void RefreshToken_with_valid_inputs_can_be_created()
    {
        var expiresAt = CreatedAt.AddHours(1);
        var token = RefreshToken.Create(LongToken(50), AccountId, CreatedAt, expiresAt);

        token.Token.Should().HaveLength(50);
        token.AccountId.Should().Be(AccountId);
        token.CreatedAt.Should().Be(CreatedAt);
        token.ExpiresAt.Should().Be(expiresAt);
        token.IsExpired.Should().BeFalse();
        token.ToString().Should().Be("[redacted]");
    }

    [Fact]
    public void RefreshToken_token_too_short_throws()
    {
        var act = () => RefreshToken.Create("short", AccountId, CreatedAt, CreatedAt.AddHours(1));

        act.Should().Throw<InvalidAccountException>();
    }

    [Fact]
    public void RefreshToken_expiration_before_creation_throws()
    {
        var act = () => RefreshToken.Create(LongToken(50), AccountId, CreatedAt, CreatedAt.AddMinutes(-1));

        act.Should().Throw<InvalidAccountException>();
    }

    [Fact]
    public void RefreshToken_exceeds_max_lifetime_throws()
    {
        var act = () => RefreshToken.Create(LongToken(50), AccountId, CreatedAt, CreatedAt.AddDays(91));

        act.Should().Throw<InvalidAccountException>();
    }

    [Fact]
    public void RefreshToken_null_account_throws()
    {
        var act = () => RefreshToken.Create(LongToken(50), null!, CreatedAt, CreatedAt.AddHours(1));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RefreshToken_is_expired_when_expires_at_in_past()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var token = RefreshToken.Create(LongToken(50), AccountId, expiresAt.AddMinutes(-10), expiresAt);

        token.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void TryCreate_with_invalid_inputs_returns_false()
    {
        var result = RefreshToken.TryCreate("short", AccountId, CreatedAt, CreatedAt.AddHours(1), out var token);

        result.Should().BeFalse();
        token.Should().BeNull();
    }
}

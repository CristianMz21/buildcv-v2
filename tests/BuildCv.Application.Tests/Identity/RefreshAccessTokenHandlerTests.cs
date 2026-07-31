using BuildCv.Application.Identity;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Application.Tests.Identity;

public class RefreshAccessTokenHandlerTests
{
    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeTokenService _tokenService = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly RefreshAccessTokenHandler _handler;

    public RefreshAccessTokenHandlerTests() =>
        _handler = new RefreshAccessTokenHandler(_refreshTokens, _accounts, _tokenService, _time);

    private async Task<Account> SeedAccountAsync()
    {
        var account = Account.Create(
            Email.Create("user@example.com"),
            Password.Create("$argon2id$v=19$m=65536,t=3,p=1$saltsalt$somehashoutputbyteslong"));
        await _accounts.AddAsync(account);
        return account;
    }

    [Fact]
    public async Task Refresh_rotates_token_and_returns_new_pair()
    {
        var account = await SeedAccountAsync();
        var now = DateTimeOffset.UtcNow;
        var oldToken = RefreshToken.Create(new string('o', 50), account.Id, now, now.AddDays(30));
        await _refreshTokens.AddAsync(oldToken);

        var result = await _handler.Handle(new RefreshAccessTokenCommand(oldToken.Token));

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccountId.Should().Be(account.Id);
        result.Value.AccessToken.Should().Be($"access-{account.Id.Value}");
        result.Value.RefreshToken.Token.Should().NotBe(oldToken.Token);
        (await _refreshTokens.GetByTokenAsync(oldToken.Token)).Should().BeNull();
        (await _refreshTokens.GetByTokenAsync(result.Value.RefreshToken.Token)).Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_expired_token_fails()
    {
        var account = await SeedAccountAsync();
        var now = DateTimeOffset.UtcNow;
        var expired = RefreshToken.Create(new string('e', 50), account.Id, now.AddDays(-10), now.AddDays(-1));
        await _refreshTokens.AddAsync(expired);

        var result = await _handler.Handle(new RefreshAccessTokenCommand(expired.Token));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Refresh token has expired.");
    }

    [Fact]
    public async Task Refresh_unknown_token_fails()
    {
        var result = await _handler.Handle(new RefreshAccessTokenCommand(new string('x', 50)));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Invalid refresh token.");
    }

    [Fact]
    public async Task Refresh_suspended_account_fails()
    {
        var account = await SeedAccountAsync();
        var now = DateTimeOffset.UtcNow;
        var token = RefreshToken.Create(new string('s', 50), account.Id, now, now.AddDays(30));
        await _refreshTokens.AddAsync(token);
        account.Suspend();

        var result = await _handler.Handle(new RefreshAccessTokenCommand(token.Token));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Invalid refresh token.");
    }
}

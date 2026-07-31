using BuildCv.Application.Identity;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Application.Tests.Identity;

public class LoginHandlerTests
{
    private const string ValidPassword = "correct-password";

    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakeTokenService _tokenService = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly LoginHandler _handler;

    public LoginHandlerTests() =>
        _handler = new LoginHandler(_accounts, _refreshTokens, _hasher, _tokenService, _time);

    private async Task<Account> SeedAccountAsync(string email = "user@example.com")
    {
        var account = Account.Create(Email.Create(email), Password.Create(_hasher.Hash(ValidPassword)));
        await _accounts.AddAsync(account);
        return account;
    }

    [Fact]
    public async Task Login_success_returns_tokens_and_records_login()
    {
        var account = await SeedAccountAsync();

        var result = await _handler.Handle(new LoginCommand("user@example.com", ValidPassword));

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccountId.Should().Be(account.Id);
        result.Value.AccessToken.Should().Be($"access-{account.Id.Value}");
        result.Value.RefreshToken.Token.Should().NotBeNullOrEmpty();
        (await _refreshTokens.GetByTokenAsync(result.Value.RefreshToken.Token)).Should().NotBeNull();
        account.LastLoginAt.Should().NotBeNull();
        account.FailedLoginCount.Should().Be(0);
    }

    [Fact]
    public async Task Login_wrong_password_fails_and_increments_failed_count()
    {
        var account = await SeedAccountAsync();

        var result = await _handler.Handle(new LoginCommand("user@example.com", "wrong-password"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Invalid credentials.");
        account.FailedLoginCount.Should().Be(1);
    }

    [Fact]
    public async Task Login_unknown_email_returns_same_generic_message_as_wrong_password()
    {
        await SeedAccountAsync();
        var wrongPassword = await _handler.Handle(new LoginCommand("user@example.com", "wrong-password"));

        var unknownEmail = await _handler.Handle(new LoginCommand("ghost@example.com", "wrong-password"));

        unknownEmail.IsSuccess.Should().BeFalse();
        unknownEmail.Error.Should().Be(wrongPassword.Error);
    }

    [Fact]
    public async Task Login_five_failed_attempts_locks_account()
    {
        var account = await SeedAccountAsync();

        for (var i = 0; i < 5; i++)
            await _handler.Handle(new LoginCommand("user@example.com", "wrong-password"));

        account.FailedLoginCount.Should().Be(5);
        account.IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task Login_locked_account_rejected_even_with_correct_password()
    {
        await SeedAccountAsync();
        for (var i = 0; i < 5; i++)
            await _handler.Handle(new LoginCommand("user@example.com", "wrong-password"));

        var result = await _handler.Handle(new LoginCommand("user@example.com", ValidPassword));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Account is temporarily locked. Try again later.");
    }

    [Fact]
    public async Task Login_suspended_account_rejected()
    {
        var account = await SeedAccountAsync();
        account.Suspend();

        var result = await _handler.Handle(new LoginCommand("user@example.com", ValidPassword));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Account is not active.");
    }
}

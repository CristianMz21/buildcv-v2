using BuildCv.Application.Identity;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Application.Tests.Identity;

public class ChangePasswordHandlerTests
{
    private const string CurrentPassword = "correct-password";
    private const string NewPassword = "brand-new-password";

    private readonly FakeAccountRepository _accounts = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly ChangePasswordHandler _handler;

    public ChangePasswordHandlerTests() =>
        _handler = new ChangePasswordHandler(_accounts, _hasher, _refreshTokens);

    private async Task<Account> SeedAccountAsync(string email = "user@example.com")
    {
        var account = Account.Create(Email.Create(email), Password.Create(_hasher.Hash(CurrentPassword)));
        await _accounts.AddAsync(account);
        return account;
    }

    private async Task<RefreshToken> SeedRefreshTokenAsync(AccountId accountId, char fill)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var token = RefreshToken.Create(new string(fill, 86), accountId, createdAt, createdAt.AddDays(30));
        await _refreshTokens.AddAsync(token);
        return token;
    }

    [Fact]
    public async Task ChangePassword_success_updates_hash_and_clears_lockout_counters()
    {
        var account = await SeedAccountAsync();
        await _handler.Handle(new ChangePasswordCommand(account.Id, "wrong-password", NewPassword));

        var result = await _handler.Handle(new ChangePasswordCommand(account.Id, CurrentPassword, NewPassword));

        result.IsSuccess.Should().BeTrue();
        _hasher.Verify(NewPassword, account.Password.Hash).Should().BeTrue();
        account.FailedLoginCount.Should().Be(0);
        account.IsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePassword_wrong_current_password_fails_and_increments_failed_count()
    {
        var account = await SeedAccountAsync();

        var result = await _handler.Handle(new ChangePasswordCommand(account.Id, "wrong-password", NewPassword));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Current password is incorrect.");
        account.FailedLoginCount.Should().Be(1);
    }

    [Fact]
    public async Task ChangePassword_five_wrong_current_passwords_locks_account()
    {
        var account = await SeedAccountAsync();

        for (var i = 0; i < 5; i++)
            await _handler.Handle(new ChangePasswordCommand(account.Id, "wrong-password", NewPassword));

        account.FailedLoginCount.Should().Be(5);
        account.IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_suspended_account_rejected_even_with_correct_current_password()
    {
        var account = await SeedAccountAsync();
        account.Suspend();

        var result = await _handler.Handle(new ChangePasswordCommand(account.Id, CurrentPassword, NewPassword));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Account is not active.");
    }

    [Fact]
    public async Task ChangePassword_locked_account_rejected_even_with_correct_current_password()
    {
        var account = await SeedAccountAsync();
        for (var i = 0; i < 5; i++)
            await _handler.Handle(new ChangePasswordCommand(account.Id, "wrong-password", NewPassword));

        var result = await _handler.Handle(new ChangePasswordCommand(account.Id, CurrentPassword, NewPassword));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Account is temporarily locked. Try again later.");
        _hasher.Verify(CurrentPassword, account.Password.Hash).Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_lockout_stops_counting_once_locked()
    {
        var account = await SeedAccountAsync();
        for (var i = 0; i < 5; i++)
            await _handler.Handle(new ChangePasswordCommand(account.Id, "wrong-password", NewPassword));

        await _handler.Handle(new ChangePasswordCommand(account.Id, "wrong-password", NewPassword));

        account.FailedLoginCount.Should().Be(5);
    }

    [Fact]
    public async Task ChangePassword_success_revokes_every_refresh_token_for_the_account()
    {
        var account = await SeedAccountAsync();
        var first = await SeedRefreshTokenAsync(account.Id, 'a');
        var second = await SeedRefreshTokenAsync(account.Id, 'b');

        var result = await _handler.Handle(new ChangePasswordCommand(account.Id, CurrentPassword, NewPassword));

        result.IsSuccess.Should().BeTrue();
        (await _refreshTokens.GetByTokenAsync(first.Token)).Should().BeNull();
        (await _refreshTokens.GetByTokenAsync(second.Token)).Should().BeNull();
    }

    [Fact]
    public async Task ChangePassword_failure_leaves_refresh_tokens_untouched()
    {
        var account = await SeedAccountAsync();
        var token = await SeedRefreshTokenAsync(account.Id, 'a');

        var result = await _handler.Handle(new ChangePasswordCommand(account.Id, "wrong-password", NewPassword));

        result.IsSuccess.Should().BeFalse();
        (await _refreshTokens.GetByTokenAsync(token.Token)).Should().Be(token);
    }

    [Fact]
    public async Task ChangePassword_does_not_revoke_other_accounts_sessions()
    {
        var account = await SeedAccountAsync();
        var bystander = await SeedRefreshTokenAsync(AccountId.New(), 'b');

        await _handler.Handle(new ChangePasswordCommand(account.Id, CurrentPassword, NewPassword));

        (await _refreshTokens.GetByTokenAsync(bystander.Token)).Should().Be(bystander);
    }

    [Fact]
    public async Task ChangePassword_unknown_account_fails()
    {
        var result = await _handler.Handle(new ChangePasswordCommand(AccountId.New(), CurrentPassword, NewPassword));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Account not found.");
    }
}

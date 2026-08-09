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

    [Fact]
    public async Task ChangePassword_weak_new_password_fails_and_leaves_the_old_one_working()
    {
        var account = await SeedAccountAsync();

        var result = await _handler.Handle(new ChangePasswordCommand(account.Id, CurrentPassword, "hunter2"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be($"Password must be at least {PasswordPolicy.MinLength} characters.");

        var persisted = await _accounts.GetByIdAsync(account.Id);
        _hasher.Verify(CurrentPassword, persisted!.Password.Hash).Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_weak_new_password_does_not_count_as_a_failed_credential_attempt()
    {
        // Choosing a password the policy refuses is a validation error, not a wrong guess at the
        // current one. Letting it reach RecordFailedLogin would let a user lock themselves out
        // of their own account by mistyping the NEW password five times.
        var account = await SeedAccountAsync();

        for (var attempt = 0; attempt < 10; attempt++)
            await _handler.Handle(new ChangePasswordCommand(account.Id, CurrentPassword, "short"));

        var persisted = await _accounts.GetByIdAsync(account.Id);
        persisted!.IsLocked.Should().BeFalse();

        var afterwards = await _handler.Handle(
            new ChangePasswordCommand(account.Id, CurrentPassword, NewPassword));
        afterwards.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_rejects_the_weak_password_before_verifying_the_current_one()
    {
        // Both hashes are Argon2id. Refusing the new password first means a request that cannot
        // succeed never buys either one.
        var account = await SeedAccountAsync();
        var before = _hasher.HashCount;

        await _handler.Handle(new ChangePasswordCommand(account.Id, CurrentPassword, "short"));

        _hasher.HashCount.Should().Be(before);
    }
}

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
    private readonly ChangePasswordHandler _handler;

    public ChangePasswordHandlerTests() =>
        _handler = new ChangePasswordHandler(_accounts, _hasher);

    private async Task<Account> SeedAccountAsync(string email = "user@example.com")
    {
        var account = Account.Create(Email.Create(email), Password.Create(_hasher.Hash(CurrentPassword)));
        await _accounts.AddAsync(account);
        return account;
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
    public async Task ChangePassword_unknown_account_fails()
    {
        var result = await _handler.Handle(new ChangePasswordCommand(AccountId.New(), CurrentPassword, NewPassword));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Account not found.");
    }
}

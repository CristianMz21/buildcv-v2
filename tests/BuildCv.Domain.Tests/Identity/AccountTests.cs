using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Identity;

public class AccountTests
{
    private static Account Build() => Account.Create(
        Email.Create("user@example.com"),
        Password.Create("$argon2id$v=19$m=65536,t=3,p=1$saltsalt$somehashoutputbyteslong"),
        Role.Candidate);

    [Fact]
    public void Account_create_sets_defaults()
    {
        var account = Build();

        account.Id.Should().NotBeNull();
        account.Status.Should().Be(AccountStatus.Active);
        account.FailedLoginCount.Should().Be(0);
        account.IsLocked.Should().BeFalse();
        account.IsEmailVerified.Should().BeFalse();
        account.Role.Should().Be(Role.Candidate);
        account.CreatedAt.Should().Be(account.UpdatedAt);
    }

    [Fact]
    public void Account_null_email_throws()
    {
        var password = Password.Create("$argon2id$v=19$m=65536,t=3,p=1$saltsalt$somehashoutputbyteslong");

        var act = () => Account.Create(null!, password);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Account_change_password_updates_password_and_timestamp()
    {
        var account = Build();
        var before = account.UpdatedAt;
        Thread.Sleep(10);

        var newPassword = Password.Create("$bcrypt$12$abcdefghijklmnopqrstuvwxyz0123456789abcdefghijkl");
        account.ChangePassword(newPassword);

        account.Password.Should().Be(newPassword);
        account.UpdatedAt.Should().BeAfter(before);
    }

    [Fact]
    public void Account_verify_email_sets_verified_flag()
    {
        var account = Build();

        account.VerifyEmail();

        account.IsEmailVerified.Should().BeTrue();
        account.EmailVerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public void Account_locks_after_five_failed_attempts()
    {
        var account = Build();

        for (var i = 0; i < 5; i++)
            account.RecordFailedLogin();

        account.IsLocked.Should().BeTrue();
        account.LockedUntil.Should().NotBeNull();
    }

    [Fact]
    public void Account_reset_lockout_clears_counter_and_lockout()
    {
        var account = Build();
        for (var i = 0; i < 5; i++)
            account.RecordFailedLogin();

        account.ResetLockout();

        account.FailedLoginCount.Should().Be(0);
        account.LockedUntil.Should().BeNull();
        account.IsLocked.Should().BeFalse();
    }

    [Fact]
    public void Account_reset_password_clears_lockout()
    {
        var account = Build();
        for (var i = 0; i < 5; i++)
            account.RecordFailedLogin();

        account.ResetPassword(Password.Create("$argon2id$v=19$m=65536,t=3,p=1$saltsalt$somehashoutputbyteslong"));

        account.FailedLoginCount.Should().Be(0);
        account.LockedUntil.Should().BeNull();
    }

    [Fact]
    public void Account_record_successful_login_clears_lockout()
    {
        var account = Build();
        for (var i = 0; i < 5; i++)
            account.RecordFailedLogin();

        account.RecordSuccessfulLogin();

        account.FailedLoginCount.Should().Be(0);
        account.LockedUntil.Should().BeNull();
        account.LastLoginAt.Should().NotBeNull();
        account.IsLocked.Should().BeFalse();
    }

    [Fact]
    public void Account_change_role_updates_role()
    {
        var account = Build();

        account.ChangeRole(Role.Admin);

        account.Role.Should().Be(Role.Admin);
    }

    [Fact]
    public void Account_can_post_jobs_requires_active_recruiter_or_admin()
    {
        var candidate = Build();
        candidate.CanPostJobs.Should().BeFalse();

        var recruiter = Build();
        recruiter.ChangeRole(Role.Recruiter);
        recruiter.CanPostJobs.Should().BeTrue();

        var lockedRecruiter = Account.Create(
            Email.Create("r@example.com"),
            Password.Create("$argon2id$v=19$m=65536,t=3,p=1$saltsalt$somehashoutputbyteslong"),
            Role.Recruiter);
        for (var i = 0; i < 5; i++)
            lockedRecruiter.RecordFailedLogin();
        lockedRecruiter.CanPostJobs.Should().BeFalse();
    }

    [Fact]
    public void Account_suspend_then_restore()
    {
        var account = Build();

        account.Suspend();
        account.Status.Should().Be(AccountStatus.Suspended);

        account.Restore();
        account.Status.Should().Be(AccountStatus.Active);
    }

    [Fact]
    public void Account_entity_equality_by_id()
    {
        var account = Build();

        account.Equals(account).Should().BeTrue();
        Build().Equals(account).Should().BeFalse();
    }
}

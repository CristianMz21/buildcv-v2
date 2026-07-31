namespace BuildCv.Domain.Identity;

using BuildCv.Domain.Common.ValueObjects;

public sealed class Account
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public AccountId Id { get; }
    public Email Email { get; }
    public Password Password { get; private set; }
    public Role Role { get; private set; }
    public AccountStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    private Account(AccountId id, Email email, Password password, Role role)
    {
        var now = DateTimeOffset.UtcNow;
        Id = id;
        Email = email;
        Password = password;
        Role = role;
        Status = AccountStatus.Active;
        CreatedAt = now;
        UpdatedAt = now;
        FailedLoginCount = 0;
    }

    public static Account Create(Email email, Password password, Role role = Role.Candidate)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);
        return new Account(AccountId.New(), email, password, role);
    }

    public void ChangePassword(Password newPassword)
    {
        ArgumentNullException.ThrowIfNull(newPassword);
        Password = newPassword;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ResetPassword(Password newPassword)
    {
        ArgumentNullException.ThrowIfNull(newPassword);
        Password = newPassword;
        FailedLoginCount = 0;
        LockedUntil = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void VerifyEmail()
    {
        EmailVerifiedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool IsEmailVerified => EmailVerifiedAt is not null;

    public void ChangeRole(Role newRole)
    {
        Role = newRole;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Suspend()
    {
        Status = AccountStatus.Suspended;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Restore()
    {
        Status = AccountStatus.Active;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Delete()
    {
        Status = AccountStatus.Deleted;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool IsLocked => LockedUntil is not null && LockedUntil > DateTimeOffset.UtcNow;

    public void RecordFailedLogin()
    {
        FailedLoginCount++;
        if (FailedLoginCount >= MaxFailedAttempts)
            LockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ResetLockout()
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordSuccessfulLogin()
    {
        LastLoginAt = DateTimeOffset.UtcNow;
        FailedLoginCount = 0;
        LockedUntil = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool CanPostJobs =>
        (Role is Role.Recruiter or Role.Admin)
        && Status == AccountStatus.Active
        && !IsLocked;

    public override bool Equals(object? obj) => obj is Account other && Id.Equals(other.Id);
    public override int GetHashCode() => Id.GetHashCode();
}

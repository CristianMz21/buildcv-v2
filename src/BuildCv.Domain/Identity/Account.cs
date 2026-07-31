using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Identity;

public sealed class Account
{
    public AccountId Id { get; }
    public Email Email { get; }
    public Password Password { get; }
    public Role Role { get; }
    public AccountStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }

    private Account(AccountId id, Email email, Password password, Role role)
    {
        Id = id;
        Email = email;
        Password = password;
        Role = role;
        Status = AccountStatus.Active;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public static Account Create(Email email, Password password, Role role = Role.Candidate)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        return new Account(AccountId.New(), email, password, role);
    }

    public void Suspend() => Status = AccountStatus.Suspended;
    public void Restore() => Status = AccountStatus.Active;
    public void Delete() => Status = AccountStatus.Deleted;

    public bool CanPostJobs =>
        Role is Role.Recruiter or Role.Admin && Status == AccountStatus.Active;
}

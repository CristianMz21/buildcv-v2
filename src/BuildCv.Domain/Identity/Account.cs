namespace BuildCv.Domain.Identity;

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;

public sealed class Account
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public AccountId Id { get; }
    public Email Email { get; }

    /// <summary>
    /// The account's password, or <c>null</c> when it has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nullable because an account created through an external identity provider genuinely has no
    /// password. The rejected alternative was to mint a random unguessable one, which needs no schema
    /// change and is worse in every other way: it leaves a live credential nobody knows, makes
    /// <see cref="HasPassword"/> unanswerable, and turns "can this account sign in with a password?"
    /// — a question five call sites ask — into a lie that always answers yes.
    /// </para>
    /// <para>
    /// <b>Null is not a weaker password, it is the absence of that credential.</b> Every path that
    /// verifies one must treat null as "no match" and must not distinguish it from a wrong password in
    /// anything the caller can observe: which accounts are external is exactly the sort of fact an
    /// enumeration attack collects.
    /// </para>
    /// </remarks>
    public Password? Password { get; private set; }

    /// <summary>Whether this account can be signed into with a password at all.</summary>
    public bool HasPassword => Password is not null;

    /// <summary>The external provider this account is linked to, or <c>null</c>.</summary>
    public string? ExternalProvider { get; private set; }

    /// <summary>
    /// The provider's own stable identifier for the person, never their address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stored because an address is not an identity.</b> Consumer Gmail addresses are never reissued,
    /// but <b>Google Workspace addresses are</b> — a company deletes <c>alice@corp.com</c> when Alice
    /// leaves and recreates it for the next Alice, who arrives with a new subject. Linking on the
    /// address alone would hand that person the previous holder's CVs: encrypted candidate data,
    /// delivered to a stranger, by design rather than by breach.
    /// </para>
    /// <para>
    /// The comparison costs nothing when the policy is on our side — if an address is genuinely never
    /// reused, <see cref="IsLinkedToDifferent"/> simply never answers true. That asymmetry is the
    /// argument: being wrong towards a refused stranger is recoverable, being wrong towards an
    /// inherited employment history is not.
    /// </para>
    /// <para>
    /// ONE PAIR RATHER THAN A COLLECTION, deliberately: this product has one provider. A second means a
    /// table, not another column — but the wire contract already reports
    /// <c>signInMethods</c> as a list, so that change is invisible to clients.
    /// </para>
    /// </remarks>
    public string? ExternalSubject { get; private set; }

    /// <summary>Whether this account is linked to an external provider at all.</summary>
    public bool HasExternalLogin => ExternalSubject is not null;

    /// <summary>
    /// Whether this account is already linked to <paramref name="provider"/> under a DIFFERENT subject —
    /// the reassigned-address case, which must be refused rather than linked.
    /// </summary>
    public bool IsLinkedToDifferent(string provider, string subject) =>
        HasExternalLogin
        && string.Equals(ExternalProvider, provider, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ExternalSubject, subject, StringComparison.Ordinal);

    /// <summary>
    /// Records the external identity this account signs in with, on first link.
    /// </summary>
    /// <remarks>
    /// Idempotent for the same subject and <b>refuses to overwrite a different one</b>: silently
    /// re-pointing an account at a new identity is exactly the takeover this field exists to prevent,
    /// and a caller that reaches here with a mismatch has skipped <see cref="IsLinkedToDifferent"/>.
    /// </remarks>
    /// <summary>
    /// The longest provider name that fits its plaintext column.
    /// </summary>
    /// <remarks>
    /// A Domain rule for a value that today comes from a closed set this code controls, because the
    /// column is bounded plaintext and every one of those needs one: without it an over-long value
    /// reaches SQL Server as error 2628, whose message quotes the offending value into a log line before
    /// <c>ValueTooLongException</c> can drop it. The translation is the net; this is the fix.
    /// </remarks>
    public const int MaxExternalProviderLength = 32;

    public void LinkExternal(string provider, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        if (provider.Length > MaxExternalProviderLength)
            throw new InvalidAccountException(
                $"External provider name must be {MaxExternalProviderLength} characters or fewer.");

        if (IsLinkedToDifferent(provider, subject))
            throw new InvalidOperationException(
                "This account is already linked to a different identity at that provider.");

        ExternalProvider = provider;
        ExternalSubject = subject;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Role Role { get; private set; }
    public AccountStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    private Account(AccountId id, Email email, Password? password, Role role)
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

    /// <summary>
    /// Creates an account whose only credential is an external identity provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The email arrives already verified, and that is the provider's claim rather than ours.</b>
    /// Google states <c>email_verified</c> on the token and refusing to record it would leave the
    /// account in a state this product has no way out of — there is no verification mail. The caller is
    /// therefore required to have checked that claim and to refuse the sign-in when it is false;
    /// stamping <see cref="EmailVerifiedAt"/> from an unverified token would be worse than having no
    /// verification at all, because every later reader would trust it.
    /// </para>
    /// <para>
    /// A separate factory rather than a nullable parameter on <see cref="Create"/>: the two differ in
    /// what they promise, not merely in what they were passed. This one asserts the address was proven
    /// by somebody else; that one asserts a password was chosen and nothing about the address.
    /// </para>
    /// </remarks>
    public static Account CreateExternal(Email email, Role role = Role.Candidate)
    {
        ArgumentNullException.ThrowIfNull(email);
        var account = new Account(AccountId.New(), email, password: null, role);
        account.EmailVerifiedAt = account.CreatedAt;
        return account;
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

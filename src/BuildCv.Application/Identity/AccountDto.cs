namespace BuildCv.Application.Identity;

using BuildCv.Domain.Identity;

/// <param name="SignInMethods">
/// Every way this account can be signed into, lowercase: <c>"password"</c> and/or a provider name.
/// </param>
public sealed record AccountDto(
    Guid Id,
    string Email,
    string Role,
    string Status,
    bool IsEmailVerified,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> SignInMethods)
{
    /// <summary>The method name reported for a password credential.</summary>
    public const string PasswordMethod = "password";

    /// <summary>
    /// Builds the DTO. <see cref="SignInMethods"/> is derived rather than stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A LIST rather than a <c>hasPassword</c> flag, and the difference is not style.</b> The
    /// question every client actually asks is "can this person still get in if they lose that
    /// provider" — which a boolean answers only while there is exactly one provider. A second one would
    /// change the contract; this does not.
    /// </para>
    /// <para>
    /// <b>It exists because a client cannot otherwise tell, and two screens are wrong without it.</b>
    /// An account created through a provider has no password, so the change-password form refuses it
    /// and the delete form asks for a credential that was never chosen. Rendering both to somebody for
    /// whom neither can work is not a rough edge: the published privacy notice promises a delete
    /// control that removes everything, and the right to erasure is not a thing to be approximately
    /// good at. This field is what lets the client say the true thing instead.
    /// </para>
    /// <para>
    /// Derived on every read rather than persisted, so it cannot disagree with the credentials it
    /// describes — a stored copy is a second statement of one fact, and the two drift the first time
    /// somebody links a provider without updating it.
    /// </para>
    /// </remarks>
    public static AccountDto From(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var methods = new List<string>(capacity: 2);
        if (account.HasPassword)
            methods.Add(PasswordMethod);
        if (account.ExternalProvider is { } provider)
            methods.Add(provider.ToLowerInvariant());

        return new AccountDto(
            account.Id.Value,
            account.Email.Value,
            account.Role.ToString(),
            account.Status.ToString(),
            account.IsEmailVerified,
            account.CreatedAt,
            account.LastLoginAt,
            methods);
    }
}

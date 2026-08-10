namespace BuildCv.Infrastructure.Security;

/// <summary>
/// Where a reset link points. The API mints the token; the page that collects the new password belongs to
/// whatever client is in front of it, so the URL cannot be hard-coded here.
/// </summary>
/// <remarks>
/// <para>
/// IT HAS A DEVELOPMENT DEFAULT AND THAT IS SAFE, unlike the JWT key or the encryption ring, which refuse
/// to start without configuration. Those are secrets, and a committed default is a key an attacker
/// already has. This is a URL: the worst a wrong one does is send somebody to a page that does not exist,
/// which is visible immediately and gives an attacker nothing.
/// </para>
/// <para>
/// The default matches the web client's development origin, which is the same value
/// <c>launchSettings.json</c> and the client's own <c>.env.example</c> already agree on.
/// </para>
/// <para>
/// <c>{token}</c> is substituted with the URL-escaped token. A template WITHOUT that placeholder is
/// refused at startup rather than silently mailing everybody the same tokenless link — the failure would
/// otherwise appear as "the reset link does not work", for every user, with nothing in the logs saying
/// why.
/// </para>
/// </remarks>
public sealed class PasswordResetSettings
{
    public const string SectionName = "PasswordReset";

    public string ResetUrlTemplate { get; init; } = "http://localhost:3000/reset-password?token={token}";
}

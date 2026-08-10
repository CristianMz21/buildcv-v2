namespace BuildCv.Infrastructure.Mail;

/// <summary>
/// Where outbound mail goes. Empty by default, which selects <see cref="UnconfiguredEmailSender"/>.
/// </summary>
/// <remarks>
/// <para>
/// SMTP RATHER THAN A PROVIDER SDK, and that is the whole point of this file. SES, Postmark, SendGrid,
/// Resend, Mailgun and a self-hosted Postfix all speak SMTP, so choosing between them becomes four
/// environment variables instead of a code change. A provider SDK would have made the choice a
/// dependency, and the choice is not this repository's to make.
/// </para>
/// <para>
/// <see cref="Host"/> IS THE SWITCH. Set it and mail is sent; leave it empty and the API answers 503 on
/// <c>POST /v1/auth/password-reset</c> and says why. There is deliberately no <c>Enabled</c> flag beside
/// it — two settings that can disagree about one fact is how a deployment ends up configured to send
/// through a host it was told to ignore.
/// </para>
/// <para>
/// <see cref="Password"/> is a secret and arrives the way every other secret here does: an environment
/// variable, never a committed file. It is validated only in the sense that a wrong one fails at send
/// time — SMTP has no way to check a credential without using it, so there is nothing this class can do
/// at startup that would not be a login attempt.
/// </para>
/// </remarks>
public sealed class SmtpSettings
{
    public const string SectionName = "Email:Smtp";

    /// <summary>Empty means no mail provider. That is the shipped default.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// 587 is submission with STARTTLS and is what almost every provider wants. 465 is implicit TLS and
    /// works too; 25 is server-to-server relay and is blocked outbound by most hosts.
    /// </summary>
    public int Port { get; init; } = 587;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// The envelope sender. It has to be an address the provider will accept for your domain — SPF and
    /// DKIM are checked against this, not against <see cref="FromName"/>.
    /// </summary>
    public string FromAddress { get; init; } = string.Empty;

    public string FromName { get; init; } = "BuildCv";

    /// <summary>
    /// Development only, and named so nobody enables it by accident. A self-signed certificate on a
    /// local MailHog or Mailpit is the case it exists for; anywhere else, accepting an unverified
    /// certificate on the connection that carries the SMTP password is the whole attack.
    /// </summary>
    public bool AllowInvalidCertificate { get; init; }
}

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BuildCv.Infrastructure.Mail;

/// <summary>
/// Sends mail over SMTP. Registered only when <c>Email:Smtp:Host</c> is set.
/// </summary>
/// <remarks>
/// <para>
/// A NEW CONNECTION PER MESSAGE, which looks wasteful and is correct here. This product sends one class
/// of mail, on a human action, at a rate the auth rate limiter already caps at five per minute per
/// address — so a pooled connection would spend its life idle, and an idle SMTP connection is one the
/// provider drops without telling the client, which turns the first send after a quiet period into a
/// failure. Reconnecting costs a TLS handshake nobody is waiting on.
/// </para>
/// <para>
/// NOTHING ABOUT THE MESSAGE REACHES A LOG. Not the recipient, not the subject, not the body — the body
/// of the one mail this system sends carries a password-reset link, which IS a credential for the
/// account. <c>ObservabilityLeakTests</c> exists because this repository treats a log line as covered by
/// none of its encryption and shipped to an aggregator with its own retention; a leaked row can be
/// re-encrypted, a leaked reset link has already been indexed. The failure path logs that a send failed
/// and the exception TYPE, never its message: an SMTP error quotes the recipient back at you.
/// </para>
/// <para>
/// It returns <see cref="Result"/> rather than throwing because the caller decides what the user is
/// told, and <c>RequestPasswordReset</c> deliberately SWALLOWS a send failure — reporting it would leak
/// whether the address has an account, since that branch is only reachable once one has been found.
/// </para>
/// </remarks>
internal sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpSettings> settings, ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// True by construction: this type is registered only when a host is configured. It still reads the
    /// setting rather than returning a literal, so a misregistration answers honestly instead of
    /// promising delivery it cannot perform.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Host);

    public async Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            var mime = new MimeMessage
            {
                Subject = message.Subject,
                Body = new TextPart("plain") { Text = message.Body }
            };
            mime.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
            mime.To.Add(MailboxAddress.Parse(message.To));

            using var client = new SmtpClient();

            if (_settings.AllowInvalidCertificate)
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;

            // StartTlsWhenAvailable, not None: it upgrades on 587 and stays plaintext only against a
            // server that offers nothing, which is the local-mailhog case. SecureSocketOptions.Auto
            // would pick implicit TLS on 465 as well, and does — this is Auto by another name for the
            // ports that matter, chosen explicitly so the intent is readable.
            await client.ConnectAsync(
                _settings.Host, _settings.Port, SecureSocketOptions.StartTlsWhenAvailable, cancellationToken);

            // Anonymous relay is a real configuration -- a local mailhog, or a provider that
            // authenticates by source address -- so an empty username is not an error.
            if (!string.IsNullOrWhiteSpace(_settings.Username))
                await client.AuthenticateAsync(_settings.Username, _settings.Password, cancellationToken);

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The TYPE, never the message. MailKit's exception text quotes the recipient and sometimes
            // the server's response, and both belong to the one person this mail is about.
            _logger.LogError("Sending an email failed: {ExceptionType}", ex.GetType().Name);
            return Result.Failure("The email could not be sent.");
        }
    }
}

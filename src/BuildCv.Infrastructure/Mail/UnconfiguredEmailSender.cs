using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using Microsoft.Extensions.Logging;

// Mail, not Email: an `Infrastructure.Email` namespace collides with the Email VALUE OBJECT, and the
// compiler reports it far from here -- "Email is a namespace but is used like a type", in
// AccountRepository and AccountEmailIndex, files this change never touched.
namespace BuildCv.Infrastructure.Mail;

/// <summary>
/// The <see cref="IEmailSender"/> that ships when no provider has been chosen. It sends nothing and says
/// so.
/// </summary>
/// <remarks>
/// <para>
/// FAILING IS THE FEATURE. The tempting alternative — return success and drop the message, or write the
/// link to the log "for development" — produces the worst outcome available: the API answers "check your
/// inbox", the user waits for a mail that will never arrive, and nobody finds out until they give up. A
/// 503 that says the feature is unavailable sends them to support on the first attempt.
/// </para>
/// <para>
/// AND THE LOG IS NOT AN OPTION HERE, whatever its convenience. A password-reset body carries a link that
/// IS a credential for the account, and <c>ObservabilityLeakTests</c> exists because this repository holds
/// that a log line is covered by none of its encryption and is shipped to an aggregator with its own
/// retention and access list. A leaked row can be re-encrypted; a leaked reset link has already been
/// indexed. So this logs that a send was refused, the subject, and nothing else — never the recipient,
/// never the body.
/// </para>
/// <para>
/// Replacing it is the whole job: register a real <see cref="IEmailSender"/> in
/// <c>AddInfrastructure</c> and every use case behind it starts working with no other change.
/// </para>
/// </remarks>
internal sealed class UnconfiguredEmailSender : IEmailSender
{
    public const string Error = "Email delivery is not configured on this server.";

    private readonly ILogger<UnconfiguredEmailSender> _logger;

    public UnconfiguredEmailSender(ILogger<UnconfiguredEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Always false. It is the whole point of this class.</summary>
    public bool IsConfigured => false;

    public Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        // Subject only. It is a fixed string this codebase wrote; To is an address and Body carries the
        // credential.
        _logger.LogWarning(
            "An email was not sent because no email provider is configured. Subject: {Subject}",
            message.Subject);

        return Task.FromResult(Result.Failure(Error));
    }
}

namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Common.ValueObjects;

/// <summary>
/// Sends one transactional email. The port exists so the use cases that need mail can be written and
/// tested before a provider is chosen.
/// </summary>
/// <remarks>
/// <para>
/// THERE IS NO REAL ADAPTER YET, and the one that ships instead FAILS rather than pretending. Choosing a
/// provider means choosing a sending domain, SPF and DKIM records and a deliverability story, which is an
/// infrastructure decision rather than a code one — so this repository states the shape and stops.
/// </para>
/// <para>
/// The failure is what keeps the product honest: a sender that silently dropped the message would let
/// <c>POST /v1/auth/password-reset</c> answer "check your inbox" to somebody whose inbox will never
/// receive anything, and they would sit there waiting instead of contacting support. See
/// <c>UnconfiguredEmailSender</c>.
/// </para>
/// <para>
/// It returns <see cref="Result"/> rather than throwing because a mail provider being down is an ordinary,
/// reportable outcome, not an exceptional one — the caller has to decide what the user is told, and an
/// exception would make that decision by escaping.
/// </para>
/// </remarks>
public interface IEmailSender
{
    /// <summary>
    /// Whether this sender can deliver at all — a property of the SERVER, never of any address.
    /// </summary>
    /// <remarks>
    /// It exists because of a bug this port had before it did. <c>RequestPasswordReset</c> answers
    /// identically for a known and an unknown address, on purpose: an endpoint that varies is an
    /// account-enumeration oracle, and having a CV on this platform means looking for work, which is a
    /// thing somebody's employer might like to know. But the mailer's failure was reachable only AFTER an
    /// account had been found — so with no provider configured, a 503 meant "that address is registered"
    /// and the whole precaution was inverted.
    ///
    /// Asking this FIRST, before the account is looked up at all, is what keeps the two answers about two
    /// different things: this one about the server, the other about nobody.
    /// </remarks>
    bool IsConfigured { get; }

    Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// One outbound message.
/// </summary>
/// <remarks>
/// PLAIN TEXT ONLY, deliberately. Every mail this product has any reason to send is a sentence and a link;
/// an HTML body would add a template language, an escaping rule and a second place for a reset link to be
/// rendered wrongly. Nothing here may reach a log — <c>Body</c> carries the reset link, which is a
/// credential, and <c>To</c> is the address this codebase hashes everywhere else it appears.
/// </remarks>
public sealed record EmailMessage(string To, string Subject, string Body);

using Microsoft.Extensions.Primitives;

namespace BuildCv.Api.Security;

/// <summary>
/// Reports, at <see cref="LogLevel.Debug"/>, what the forwarded-header chain actually resolved to.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ForwardedHeadersSettings"/> tells an operator to enable trust only once it is verified
/// against the real ingress rather than assumed — and until this existed there was no way to do the
/// verifying. Enabling it produces no output, no error and no visible change: the peer address is
/// simply a different value, indistinguishable from the one it replaced unless you already know what
/// the client's address was. The one measurement taken against a real deployment recorded an
/// environment-internal address with trust both on and off, which is consistent with three different
/// causes and distinguishes none of them.
/// </para>
/// <para>
/// So this logs the three facts that do distinguish them: what the peer was before
/// <c>UseForwardedHeaders</c> ran, what it is after, and what remains unconsumed in the chain.
/// <c>ForwardedHeadersMiddleware</c> preserves the first as <c>X-Original-For</c> and truncates the
/// entries it consumed off <c>X-Forwarded-For</c>, so reading both after it has run says how many
/// hops were really there — which is the number <c>ForwardLimit</c> is supposed to equal and is
/// otherwise a guess.
/// </para>
/// <para>
/// <b>Debug level, so it is off in production by default</b> and costs one <c>IsEnabled</c> check.
/// Turn it on for one request, read the answer, turn it off: an address is personal data under GDPR,
/// and a log line is covered by none of this repository's encryption. That is the same reason it
/// reports addresses and never a body, a cookie or a header outside this family.
/// </para>
/// <para>
/// <b>Every logged value is sanitized first, because all of them are attacker-controlled.</b>
/// <c>X-Forwarded-For</c> is client input by definition — that is the entire premise of
/// <see cref="ForwardedHeadersSettings"/> — and it is being written into an aggregator that will
/// index it. An unsanitized value can forge a second log line, and this is the one log line in the
/// repository whose input is *designed* to come from outside. Values are <b>replaced</b> rather than
/// trimmed or stripped, the same ruling <c>CorrelationIdMiddleware</c> makes and for the same reason:
/// a stripped value reports a chain nobody sent while looking exactly like one they did.
/// </para>
/// </remarks>
public sealed class ForwardedHeaderDiagnostics(RequestDelegate next, ILogger<ForwardedHeaderDiagnostics> logger)
{
    /// <summary>What <c>ForwardedHeadersMiddleware</c> saves the pre-existing peer address as.</summary>
    private const string OriginalForHeader = "X-Original-For";

    private const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>Stand-in for a value that could not be logged safely.</summary>
    public const string Unsafe = "<unsafe>";

    /// <summary>Stand-in for a header that was not present at all.</summary>
    public const string Absent = "<absent>";

    /// <summary>
    /// Ten hops of IPv6-with-port is roughly 500 characters, so this is generous for anything real
    /// while refusing the 8 KB header a caller is free to send.
    /// </summary>
    public const int MaxLength = 256;

    private readonly RequestDelegate _next = next;
    private readonly ILogger<ForwardedHeaderDiagnostics> _logger = logger;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The guard is the feature: on a production deployment this is the only work done, and it is
        // a field read. Formatting the values before the check would pay for a line nobody emits.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Forwarded-header resolution: peer is now {Peer}, was {OriginalPeer} before trust ran; "
                + "unconsumed {ForwardedForHeader} is {ForwardedFor}.",
                ClientAddress.Describe(context),
                Sanitize(context.Request.Headers[OriginalForHeader]),
                ForwardedForHeader,
                Sanitize(context.Request.Headers[ForwardedForHeader]));
        }

        return _next(context);
    }

    /// <summary>
    /// Renders a forwarded-header value for a log line, or refuses to.
    /// </summary>
    /// <remarks>
    /// The accepted set is exactly what an address list can contain: hex digits and dots and colons
    /// for the addresses themselves, brackets for a bracketed IPv6 literal, and a comma and space
    /// for the separators. A value holding anything else is not a chain this code should be
    /// paraphrasing, so it is replaced whole.
    /// <para>
    /// A repeated header is refused rather than joined. <see cref="StringValues.ToString()"/> joins
    /// with a comma, which is also the chain's own separator — so two headers of one hop each would
    /// render identically to one header of two hops, and this line exists precisely to count hops.
    /// </para>
    /// </remarks>
    public static string Sanitize(StringValues value)
    {
        if (value.Count == 0)
            return Absent;

        if (value.Count > 1)
            return Unsafe;

        var text = value.ToString();

        if (text.Length == 0)
            return Absent;

        if (text.Length > MaxLength)
            return Unsafe;

        foreach (var character in text)
        {
            if (!IsAddressCharacter(character))
                return Unsafe;
        }

        return text;
    }

    private static bool IsAddressCharacter(char character) =>
        character is >= '0' and <= '9'
        || character is >= 'a' and <= 'f'
        || character is >= 'A' and <= 'F'
        || character is '.' or ':' or '[' or ']' or ',' or ' ';
}

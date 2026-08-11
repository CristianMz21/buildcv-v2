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
/// entries it consumed off <c>X-Forwarded-For</c>, so what is left says whether the chain outgrew
/// <c>ForwardLimit</c> — and if it did, by how many.
/// </para>
/// <para>
/// <b>It does not count the chain.</b> This reports what the middleware LEFT, not what ARRIVED, so an
/// empty remainder means "no longer than <c>ForwardLimit</c>" rather than "exactly
/// <c>ForwardLimit</c>" — a shorter chain leaves the header equally empty and resolves to the same
/// address. That weaker claim is the one worth having: a leftover entry is precisely where a caller's
/// forged value would be sitting, so an empty remainder is the safety property and an exact hop count
/// is not.
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

    /// <summary>
    /// Single-value client-address headers, reported alongside the chain and <b>never trusted</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These exist because of what the chain measurement found: Azure Container Apps' external ingress
    /// <b>replaces</b> <c>X-Forwarded-For</c> with the address it sees rather than appending to it. That
    /// is what makes forged chains harmless — but put a CDN in front and the address it sees is the
    /// CDN's, so the real client is discarded before the API can read it. `ForwardLimit` cannot recover
    /// it: there is no entry left to unwind to.
    /// </para>
    /// <para>
    /// A CDN's own single-value header may survive that rewrite, since the ingress rewrites only
    /// <c>X-Forwarded-For</c>. <b>May</b> — which is the entire reason this list is observational. It
    /// answers "does the client address reach this process at all, under any name", and nothing here
    /// reads these into <see cref="HttpContext.Connection"/> or any partition key. Trusting one is a
    /// separate decision that needs its own allowlist, and it should not be taken before a reading
    /// shows the header arriving.
    /// </para>
    /// <para>
    /// A <b>closed list</b>, not configuration: a caller-nameable header would turn a debug switch into
    /// "log me an arbitrary request header". These three cover the edges this deployment has actually
    /// had in front of it — Cloudflare, its Enterprise/Akamai spelling, and the nginx-derived one most
    /// proxies emit. <b>They are not the whole convention</b>: <c>Fastly-Client-IP</c>,
    /// <c>X-Client-IP</c> and <c>X-Cluster-Client-IP</c> exist too. Adding a name is a one-line change
    /// and should be made when a deployment gains an edge that uses one, rather than pre-emptively —
    /// a name that no edge in front ever sets reports <c>&lt;absent&gt;</c> forever and reads like an
    /// answer.
    /// </para>
    /// <para>
    /// <b>They are not equally trustworthy, and the difference was measured rather than assumed.</b>
    /// Through a Cloudflare-proxied hostname, a client-supplied <c>CF-Connecting-IP</c> is refused by
    /// Cloudflare itself with a 403 — its own, distinguishable from this API's because ours are
    /// <c>application/problem+json</c>. A client-supplied <c>True-Client-IP</c> passed **straight
    /// through** to the origin in the same experiment. So one of these three is forgeable by any caller
    /// on exactly the deployment where it looks most authoritative. Nothing here reads any of them, and
    /// anything that ever does must name which edge writes it and verify that edge overwrites a
    /// supplied value — for each header separately, on the deployment in question.
    /// </para>
    /// </remarks>
    public static readonly string[] ClientAddressHeaders =
        ["CF-Connecting-IP", "True-Client-IP", "X-Real-IP"];

    /// <summary>Stand-in for a value that could not be logged safely.</summary>
    public const string Unsafe = "<unsafe>";

    /// <summary>Stand-in for a header that was not present at all.</summary>
    public const string Absent = "<absent>";

    /// <summary>
    /// Holds about five hops of worst-case IPv6-with-port and refuses more.
    /// </summary>
    /// <remarks>
    /// A bracketed IPv6 literal with a port is 47 characters at its widest, so 256 fits five of them
    /// with separators (243) and not six. Real chains are two to four hops — the measured one here is
    /// two — so this is generous for anything a deployment produces while refusing the 8 KB header a
    /// caller is free to send.
    /// <para>
    /// A longer chain is reported as <see cref="Unsafe"/> rather than truncated, which is the same
    /// ruling the character check makes: a truncated chain has a different hop count from the one that
    /// arrived, and hop count is the thing this line exists to report.
    /// </para>
    /// </remarks>
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
                + "unconsumed {ForwardedForHeader} is {ForwardedFor}. Untrusted single-value headers: "
                + "{ClientAddressHeaders}.",
                ClientAddress.Describe(context),
                Sanitize(context.Request.Headers[OriginalForHeader]),
                ForwardedForHeader,
                Sanitize(context.Request.Headers[ForwardedForHeader]),
                DescribeClientAddressHeaders(context.Request.Headers));
        }

        return _next(context);
    }

    /// <summary>
    /// Renders every header in <see cref="ClientAddressHeaders"/> as one <c>name=value</c> string.
    /// </summary>
    /// <remarks>
    /// One string rather than one placeholder each, so adding a name to the list does not change the
    /// message template — the template is what a log aggregator groups on, and a template that shifts
    /// whenever the list grows splits one line into two unrelated series. Every value goes through
    /// <see cref="Sanitize"/>: these are as caller-supplied as <c>X-Forwarded-For</c> is, and reporting
    /// one is not the same as believing it.
    /// </remarks>
    public static string DescribeClientAddressHeaders(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return string.Join(
            ' ',
            ClientAddressHeaders.Select(name => $"{name}={Sanitize(headers[name])}"));
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

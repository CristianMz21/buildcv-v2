using System.Net;

namespace BuildCv.Api.Security;

/// <summary>
/// Single source of truth for "which client is this request from".
/// </summary>
/// <remarks>
/// The peer address is only the real client after <c>UseForwardedHeaders</c> has run with a
/// configured proxy allowlist (see <see cref="ForwardedHeadersSettings"/>). Nothing here reads
/// <c>X-Forwarded-For</c> directly: a raw client-supplied header must never reach a rate-limit
/// partition key.
/// </remarks>
public static class ClientAddress
{
    /// <summary>Stand-in used when the peer address is unavailable.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Collapses the two spellings of one IPv4 client into one. A dual-stack socket reports IPv4
    /// peers as <c>::ffff:a.b.c.d</c> while <c>ForwardedHeadersMiddleware</c> produces plain IPv4;
    /// without this, the same client would hold two rate-limit buckets and audit lines would not
    /// line up with the 429s they explain.
    /// </summary>
    public static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>
    /// Full-precision, normalized rendering for audit trails. Deliberately not the rate-limit
    /// partition key: forensics wants the exact address, throttling wants the whole allocation.
    /// </summary>
    public static string Describe(IPAddress? address) => Normalize(address)?.ToString() ?? Unknown;

    public static string Describe(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Describe(context.Connection.RemoteIpAddress);
    }
}

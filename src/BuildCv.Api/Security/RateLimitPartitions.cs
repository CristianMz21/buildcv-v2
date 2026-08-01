using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;

namespace BuildCv.Api.Security;

/// <summary>
/// Partition keys for the IP-scoped rate limiters.
/// </summary>
/// <remarks>
/// The peer address is only trustworthy after <c>UseForwardedHeaders</c> has run with a configured
/// proxy allowlist (see <see cref="ForwardedHeadersSettings"/>). Nothing here reads
/// <c>X-Forwarded-For</c> directly: partitioning on a raw client-supplied header would let any
/// caller mint a fresh bucket per request.
/// </remarks>
public static class RateLimitPartitions
{
    /// <summary>
    /// Shared bucket for requests with no peer address. Collapsing them is deliberate — handing
    /// each unidentifiable request its own partition would be an unlimited bypass, so this fails
    /// closed.
    /// </summary>
    public const string UnknownClient = "unknown";

    public static string ClientKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ClientKey(context.Connection.RemoteIpAddress);
    }

    public static string ClientKey(IPAddress? address)
    {
        if (address is null)
            return UnknownClient;

        // Kestrel reports IPv4 peers as ::ffff:a.b.c.d on a dual-stack socket, while
        // ForwardedHeadersMiddleware parses X-Forwarded-For into plain IPv4. Without normalizing,
        // the same client gets two buckets depending on how it reached the app — and an attacker
        // could double their budget by alternating the two encodings.
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return normalized.ToString();
    }
}

public static class RateLimitResponse
{
    private const string FallbackRetryAfterSeconds = "60";

    public static void SetRetryAfter(HttpResponse response, RateLimitLease lease)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(lease);

        response.Headers.RetryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
            : FallbackRetryAfterSeconds;
    }
}

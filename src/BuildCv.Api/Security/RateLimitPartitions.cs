using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace BuildCv.Api.Security;

/// <summary>
/// Partition keys for the IP-scoped rate limiters.
/// </summary>
/// <remarks>
/// A partition key has to name something the caller cannot cheaply change. Anything else is a
/// bypass: pick a fresh key per request and every limiter in the app becomes decorative. The peer
/// address only qualifies once <c>UseForwardedHeaders</c> has run with a configured proxy
/// allowlist (see <see cref="ForwardedHeadersSettings"/>); nothing here reads
/// <c>X-Forwarded-For</c> directly.
/// </remarks>
public static class RateLimitPartitions
{
    /// <summary>Width of an IPv6 partition, in bits. See <see cref="ClientKey(IPAddress?)"/>.</summary>
    private const int IPv6PrefixBits = 64;

    /// <summary>
    /// Shared bucket for requests with no peer address. Collapsing them is deliberate — handing
    /// each unidentifiable request its own partition would be an unlimited bypass, so this fails
    /// closed.
    /// </summary>
    public const string UnknownClient = ClientAddress.Unknown;

    public static string ClientKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ClientKey(context.Connection.RemoteIpAddress);
    }

    public static string ClientKey(IPAddress? address)
    {
        var normalized = ClientAddress.Normalize(address);

        if (normalized is null)
            return UnknownClient;

        // IPv4 is handed out an address at a time, so /32 is the right granularity. IPv6 is not: a
        // residential line or a VPS is routinely delegated a whole /64, which is 2^64 addresses one
        // party can source at will. Keying on the full /128 would let that party mint a fresh
        // bucket per request and walk straight through both the auth window and the global limiter
        // — no proxy required. Truncating charges the allocation instead of the address. Wider
        // delegations (a /48 or /56 to one customer) can still hold more than one bucket; /64 is
        // the smallest prefix that is never split across customers.
        return normalized.AddressFamily == AddressFamily.InterNetworkV6
            ? IPv6PartitionKey(normalized)
            : normalized.ToString();
    }

    private static string IPv6PartitionKey(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written) || written != bytes.Length)
            return UnknownClient;

        // Zero the interface identifier, keep the routing prefix.
        bytes[(IPv6PrefixBits / 8)..].Clear();

        // The suffix stops a prefix key from ever colliding with an exact-address key.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{new IPAddress(bytes)}/{IPv6PrefixBits}");
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

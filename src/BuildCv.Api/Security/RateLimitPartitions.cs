using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using BuildCv.Api.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

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

/// <summary>
/// The 429 this API answers, written in one place so the middleware limiters and the account-scoped
/// ones cannot drift into two different refusals for one class of event.
/// </summary>
public static class RateLimitResponse
{
    private const string FallbackRetryAfterSeconds = "60";

    /// <summary>
    /// The detail carried by the 429 the rate-limiting MIDDLEWARE writes — the global 100/min limiter
    /// and the <c>auth</c> and <c>logout</c> policies.
    /// </summary>
    /// <remarks>
    /// Deliberately generic. <c>OnRejectedContext</c> carries only the <c>HttpContext</c> and the failed
    /// lease, so the callback cannot name the limiter that refused (the same reason the metric tag is
    /// derived from endpoint metadata rather than from the lease). The three account-scoped limiters are
    /// acquired inside their endpoints, where the caller IS known, so those keep their specific details.
    /// </remarks>
    public const string ThrottledDetail = "Too many requests.";

    public static void SetRetryAfter(HttpResponse response, RateLimitLease lease)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(lease);

        response.Headers.RetryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
            : FallbackRetryAfterSeconds;
    }

    /// <summary>
    /// Writes the ProblemDetails body for a middleware-rejected request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WITHOUT THIS, <c>options.OnRejected</c> set a header and returned, so a 429 from the global,
    /// <c>auth</c> or <c>logout</c> limiter carried <c>Content-Length: 0</c> and no content type, while
    /// the three account-scoped limiters answered real ProblemDetails through <c>Results.Problem</c> —
    /// one class of refusal, two bodies. It was an OMISSION, not one of the two refusals this API
    /// documents as unshapeable: the 413 and the unterminated-multipart 400 are torn down inside Kestrel
    /// and minimal-API form binding respectively, before any code here can run. This one had somewhere
    /// to run all along.
    /// </para>
    /// <para>
    /// The status code is NOT assigned here. <c>RateLimiterOptions.RejectionStatusCode</c> is the single
    /// place it is stated, and the middleware applies it BEFORE invoking <c>OnRejected</c> — measured, by
    /// asserting the status alongside a parsed body: were the order the other way round, writing here
    /// would start the response and the later assignment would throw, turning the 429 into a 500.
    /// </para>
    /// <para>
    /// <c>contentType</c> is passed to <c>WriteAsJsonAsync</c> rather than assigned to
    /// <c>Response.ContentType</c> first, for the reason <see cref="ProblemDetailsContentType"/> records:
    /// the overload without it overwrites the assignment with <c>application/json</c>.
    /// </para>
    /// </remarks>
    public static Task WriteProblemAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Title = ReasonPhrases.GetReasonPhrase(StatusCodes.Status429TooManyRequests),
                Detail = ThrottledDetail,
                Status = StatusCodes.Status429TooManyRequests
            },
            options: null,
            contentType: ProblemDetailsContentType.Value,
            cancellationToken);
    }
}

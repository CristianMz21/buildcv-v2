using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace BuildCv.Api.Security;

/// <summary>
/// Deployment-scoped configuration for trusting <c>X-Forwarded-*</c>, bound from
/// <c>Network:ForwardedHeaders</c>. Disabled by default.
/// </summary>
/// <remarks>
/// <para>
/// Rate limiting partitions on the peer address. Behind a reverse proxy, ingress, or CDN every
/// request arrives from the proxy, so all clients collapse into one partition and the 5/min auth
/// window becomes a global 5/min cap for the whole deployment — a self-inflicted denial of service
/// that also throttles nobody in particular.
/// </para>
/// <para>
/// The fix is not to trust <c>X-Forwarded-For</c> unconditionally. That header is client-controlled
/// input: with no allowlist, any caller can claim a fresh source address on every request, mint a
/// new partition each time, and remove throttling entirely — strictly worse than the collapsed
/// partition. Enabling this is therefore a deployment decision that must name the proxies that are
/// allowed to speak for their clients.
/// </para>
/// </remarks>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "Network:ForwardedHeaders";

    public bool Enabled { get; set; }

    /// <summary>Exact proxy addresses allowed to set <c>X-Forwarded-*</c>.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>CIDR ranges allowed to set <c>X-Forwarded-*</c>, e.g. <c>10.0.0.0/8</c>.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>How many proxy hops to unwind. Keep this equal to the real hop count.</summary>
    public int ForwardLimit { get; set; } = 1;
}

public static class ForwardedHeadersConfiguration
{
    public static ForwardedHeadersSettings Read(IConfiguration configuration) =>
        configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>()
        ?? new ForwardedHeadersSettings();

    /// <summary>
    /// Builds the middleware options, failing fast on a configuration that would either be
    /// ineffective or unsafe.
    /// </summary>
    public static ForwardedHeadersOptions Build(ForwardedHeadersSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Enabling without an allowlist would silently do nothing, and an operator who believes
        // client IPs are being restored while they are not has the worst of both worlds. Refusing
        // to start is the honest answer.
        if (settings.KnownProxies.Length == 0 && settings.KnownNetworks.Length == 0)
        {
            throw new InvalidOperationException(
                $"{ForwardedHeadersSettings.SectionName}:Enabled is true but neither KnownProxies nor "
                + "KnownNetworks is configured. Forwarded headers from unlisted peers are attacker-controlled "
                + "and would let any client pick its own rate-limit partition.");
        }

        if (settings.ForwardLimit < 1)
        {
            throw new InvalidOperationException(
                $"{ForwardedHeadersSettings.SectionName}:ForwardLimit must be at least 1.");
        }

        var options = new ForwardedHeadersOptions
        {
            // Host is deliberately excluded: a forwarded Host is a link-generation and
            // cache-poisoning surface, and nothing here needs it.
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = settings.ForwardLimit
        };

        // The framework defaults trust IPv6 loopback. Deployments sit behind a named proxy, so the
        // allowlist is replaced wholesale rather than extended.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in settings.KnownProxies)
            options.KnownProxies.Add(ParseAddress(proxy));

        foreach (var network in settings.KnownNetworks)
            options.KnownIPNetworks.Add(ParseNetwork(network));

        return options;
    }

    private static IPAddress ParseAddress(string value) =>
        IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException(
                $"'{value}' is not a valid IP address in {ForwardedHeadersSettings.SectionName}:KnownProxies.");

    // Fully qualified: Microsoft.AspNetCore.HttpOverrides also exports an obsolete IPNetwork.
    private static System.Net.IPNetwork ParseNetwork(string value) =>
        System.Net.IPNetwork.TryParse(value, out var network)
            ? network
            : throw new InvalidOperationException(
                $"'{value}' is not a valid CIDR range in {ForwardedHeadersSettings.SectionName}:KnownNetworks.");
}

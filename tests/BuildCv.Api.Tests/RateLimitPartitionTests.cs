using System.Net;
using BuildCv.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BuildCv.Api.Tests;

// The rate limiters partition on the peer address, so these tests pin the two halves that decide
// what that address is: the key derivation itself, and whether X-Forwarded-For is allowed to
// change it. Trusting that header without an allowlist would let any caller pick its own bucket.
public sealed class RateLimitPartitionTests
{
    private const string ProxyAddress = "10.0.0.5";
    private const string ClientIp = "203.0.113.7";

    private static ForwardedHeadersSettings TrustingProxy() => new()
    {
        Enabled = true,
        KnownProxies = [ProxyAddress]
    };

    private static HttpContext ProxiedRequest(string peer)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = ClientIp;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        return context;
    }

    private static async Task<HttpContext> RunForwardedHeadersAsync(ForwardedHeadersSettings settings, HttpContext context)
    {
        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(ForwardedHeadersConfiguration.Build(settings)));

        await middleware.Invoke(context);
        return context;
    }

    [Fact]
    public void ClientKey_WithoutPeerAddress_CollapsesToTheSharedUnknownPartition()
    {
        RateLimitPartitions.ClientKey((IPAddress?)null).Should().Be(RateLimitPartitions.UnknownClient);
    }

    // A dual-stack socket reports IPv4 peers as ::ffff:a.b.c.d while ForwardedHeadersMiddleware
    // produces plain IPv4. Two keys for one client would double the attacker's budget.
    [Fact]
    public void ClientKey_IPv4MappedToIPv6_MatchesThePlainIPv4Partition()
    {
        var mapped = RateLimitPartitions.ClientKey(IPAddress.Parse("::ffff:203.0.113.7"));
        var plain = RateLimitPartitions.ClientKey(IPAddress.Parse(ClientIp));

        mapped.Should().Be(plain).And.Be(ClientIp);
    }

    [Fact]
    public void ClientKey_DifferentAddresses_ProduceDifferentPartitions()
    {
        RateLimitPartitions.ClientKey(IPAddress.Parse("203.0.113.7"))
            .Should().NotBe(RateLimitPartitions.ClientKey(IPAddress.Parse("203.0.113.8")));
    }

    [Fact]
    public void ClientKey_IPv4_KeepsFullAddressPrecision()
    {
        RateLimitPartitions.ClientKey(IPAddress.Parse(ClientIp)).Should().Be(ClientIp);
    }

    // A residential line or a VPS is routinely delegated an entire IPv6 /64. Keying on the full
    // /128 would let one party source 2^64 addresses, mint a fresh bucket per request, and walk
    // through both the auth window and the global limiter with no proxy involved.
    [Fact]
    public void ClientKey_IPv6AddressesInTheSameSlashSixtyFour_SharePartition()
    {
        var low = RateLimitPartitions.ClientKey(IPAddress.Parse("2001:db8:1:2::1"));
        var high = RateLimitPartitions.ClientKey(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff"));

        low.Should().Be(high);
    }

    [Fact]
    public void ClientKey_IPv6AddressesInDifferentSlashSixtyFours_DoNotSharePartition()
    {
        var first = RateLimitPartitions.ClientKey(IPAddress.Parse("2001:db8:1:2::1"));
        var second = RateLimitPartitions.ClientKey(IPAddress.Parse("2001:db8:1:3::1"));

        first.Should().NotBe(second);
    }

    // The prefix key must not be spellable as an exact address, or a client sitting on the base
    // address of its own /64 would collide with the truncated key of everyone else in it.
    [Fact]
    public void ClientKey_IPv6PartitionIsMarkedAsAPrefix()
    {
        RateLimitPartitions.ClientKey(IPAddress.Parse("2001:db8:1:2::5")).Should().Be("2001:db8:1:2::/64");
    }

    // Throttling charges the whole allocation; forensics needs the exact address inside it.
    [Fact]
    public void Describe_KeepsFullIPv6PrecisionForAuditTrails()
    {
        ClientAddress.Describe(IPAddress.Parse("2001:db8:1:2::5")).Should().Be("2001:db8:1:2::5");
    }

    [Fact]
    public void Describe_NormalizesIPv4MappedIPv6LikeThePartitionKeyDoes()
    {
        ClientAddress.Describe(IPAddress.Parse("::ffff:203.0.113.7")).Should().Be(ClientIp);
    }

    [Fact]
    public void Describe_WithoutAnAddress_ReadsUnknown()
    {
        ClientAddress.Describe((IPAddress?)null).Should().Be(RateLimitPartitions.UnknownClient);
    }

    [Fact]
    public async Task ForwardedHeaders_FromKnownProxy_PartitionsOnTheForwardedClient()
    {
        var context = await RunForwardedHeadersAsync(TrustingProxy(), ProxiedRequest(ProxyAddress));

        RateLimitPartitions.ClientKey(context).Should().Be(ClientIp);
    }

    [Fact]
    public async Task ForwardedHeaders_FromKnownProxy_AppliesTheForwardedScheme()
    {
        var context = await RunForwardedHeadersAsync(TrustingProxy(), ProxiedRequest(ProxyAddress));

        context.Request.Scheme.Should().Be("https");
    }

    // The threat the allowlist exists for: a client that is not a configured proxy must not be
    // able to claim a source address and mint itself a fresh rate-limit bucket per request.
    [Fact]
    public async Task ForwardedHeaders_FromUnlistedPeer_IgnoresTheSpoofedAddress()
    {
        var context = await RunForwardedHeadersAsync(TrustingProxy(), ProxiedRequest("198.51.100.9"));

        RateLimitPartitions.ClientKey(context).Should().Be("198.51.100.9");
        context.Request.Scheme.Should().Be("http");
    }

    [Fact]
    public async Task ForwardedHeaders_FromKnownNetwork_PartitionsOnTheForwardedClient()
    {
        var settings = new ForwardedHeadersSettings { Enabled = true, KnownNetworks = ["10.0.0.0/8"] };

        var context = await RunForwardedHeadersAsync(settings, ProxiedRequest(ProxyAddress));

        RateLimitPartitions.ClientKey(context).Should().Be(ClientIp);
    }

    [Fact]
    public async Task ForwardedHeaders_FromOutsideTheKnownNetwork_IgnoresTheSpoofedAddress()
    {
        var settings = new ForwardedHeadersSettings { Enabled = true, KnownNetworks = ["10.0.0.0/8"] };

        var context = await RunForwardedHeadersAsync(settings, ProxiedRequest("172.16.0.4"));

        RateLimitPartitions.ClientKey(context).Should().Be("172.16.0.4");
    }

    // Enabling the feature with nothing to trust would silently ignore every forwarded header
    // while the operator believes client IPs are being restored. Refuse to start instead.
    [Fact]
    public void Build_EnabledWithoutAnyAllowlistEntry_Throws()
    {
        var settings = new ForwardedHeadersSettings { Enabled = true };

        var build = () => ForwardedHeadersConfiguration.Build(settings);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*KnownProxies*KnownNetworks*");
    }

    [Fact]
    public void Build_ReplacesTheLoopbackDefaults()
    {
        var settings = new ForwardedHeadersSettings
        {
            Enabled = true,
            KnownProxies = [ProxyAddress],
            KnownNetworks = ["10.0.0.0/8"]
        };

        var options = ForwardedHeadersConfiguration.Build(settings);

        options.KnownProxies.Should().ContainSingle().Which.Should().Be(IPAddress.Parse(ProxyAddress));
        options.KnownIPNetworks.Should().ContainSingle()
            .Which.Should().Be(System.Net.IPNetwork.Parse("10.0.0.0/8"));
    }

    // A forwarded Host is a link-generation and cache-poisoning surface and nothing here needs it.
    [Fact]
    public void Build_DoesNotTrustForwardedHost()
    {
        var options = ForwardedHeadersConfiguration.Build(TrustingProxy());

        options.ForwardedHeaders.Should().Be(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        options.ForwardLimit.Should().Be(1);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/8")]
    public void Build_WithMalformedKnownProxy_Throws(string proxy)
    {
        var settings = new ForwardedHeadersSettings { Enabled = true, KnownProxies = [proxy] };

        var build = () => ForwardedHeadersConfiguration.Build(settings);

        build.Should().Throw<InvalidOperationException>().WithMessage("*KnownProxies*");
    }

    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/999")]
    public void Build_WithMalformedKnownNetwork_Throws(string network)
    {
        var settings = new ForwardedHeadersSettings { Enabled = true, KnownNetworks = [network] };

        var build = () => ForwardedHeadersConfiguration.Build(settings);

        build.Should().Throw<InvalidOperationException>().WithMessage("*KnownNetworks*");
    }

    [Fact]
    public void Build_WithZeroForwardLimit_Throws()
    {
        var settings = new ForwardedHeadersSettings
        {
            Enabled = true,
            KnownProxies = [ProxyAddress],
            ForwardLimit = 0
        };

        var build = () => ForwardedHeadersConfiguration.Build(settings);

        build.Should().Throw<InvalidOperationException>().WithMessage("*ForwardLimit*");
    }

    [Fact]
    public void Read_WithoutTheSection_LeavesForwardedHeadersDisabled()
    {
        var configuration = new ConfigurationBuilder().Build();

        var settings = ForwardedHeadersConfiguration.Read(configuration);

        settings.Enabled.Should().BeFalse();
        settings.KnownProxies.Should().BeEmpty();
        settings.KnownNetworks.Should().BeEmpty();
    }

    [Fact]
    public void Read_BindsTheConfiguredAllowlist()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Network:ForwardedHeaders:Enabled"] = "true",
                ["Network:ForwardedHeaders:KnownProxies:0"] = ProxyAddress,
                ["Network:ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8",
                ["Network:ForwardedHeaders:ForwardLimit"] = "2"
            })
            .Build();

        var settings = ForwardedHeadersConfiguration.Read(configuration);

        settings.Enabled.Should().BeTrue();
        settings.KnownProxies.Should().Equal(ProxyAddress);
        settings.KnownNetworks.Should().Equal("10.0.0.0/8");
        settings.ForwardLimit.Should().Be(2);
    }
}

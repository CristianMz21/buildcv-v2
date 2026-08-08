using System.Net;
using BuildCv.Api.Health;
using BuildCv.Application.Common.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

// The point of two probes is that they answer DIFFERENTLY when the store is down, so the tests here
// are built around that asymmetry rather than around either endpoint's happy path. A suite that only
// asserted "GET /health/live is 200" would pass just as happily against one endpoint mapped twice.
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Live_AnswersHealthyToAnAnonymousCaller()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync(HealthEndpoints.LivePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
        // Pinned because DatabaseHealthCheck's descriptions are written on the assumption that the
        // default writer emits the STATUS ONLY. A custom ResponseWriter that serialized the entries
        // would start publishing every check's description to an anonymous caller, and this line is
        // what makes that a decision instead of a side effect.
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
    }

    [Fact]
    public async Task Ready_AnswersHealthyWhenTheStoreAnswers()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync(HealthEndpoints.ReadyPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    // THE TEST THIS PAIR OF ENDPOINTS EXISTS FOR. One host, one unreachable store, two different
    // answers — and both are asserted against the SAME factory, so neither can be explained by the two
    // requests having reached differently-configured processes.
    [Fact]
    public async Task WhenTheStoreIsUnreachable_ReadyFails_AndLiveDoesNot()
    {
        var probe = new UnreachableProbe();
        using var factory = new ApiTestFactory(
            configureServices: services => services.AddSingleton<IPersistenceProbe>(probe));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var ready = await client.GetAsync(HealthEndpoints.ReadyPath);
        var live = await client.GetAsync(HealthEndpoints.LivePath);

        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a readiness probe reports a dependency it cannot reach, which takes the instance out of rotation");
        (await ready.Content.ReadAsStringAsync()).Should().Be("Unhealthy");

        live.StatusCode.Should().Be(HttpStatusCode.OK,
            "a liveness failure RESTARTS the process, and restarting cannot fix a database that is down");
        (await live.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    // The status codes above are a small closed set, so they are weak evidence on their own: /health/live
    // would answer 200 whether it skipped the probe or called a probe that happened to succeed. This
    // COUNTS the calls instead. Zero is what "no dependencies touched" actually means, and it is a claim
    // no status code can make.
    [Fact]
    public async Task Live_ConsultsNoProbeAtAll_WhileReadyConsultsItEveryTime()
    {
        var probe = new CountingProbe();
        using var factory = new ApiTestFactory(
            configureServices: services => services.AddSingleton<IPersistenceProbe>(probe));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        for (var i = 0; i < 3; i++)
            (await client.GetAsync(HealthEndpoints.LivePath)).EnsureSuccessStatusCode();

        probe.Calls.Should().Be(0, "liveness must touch nothing outside the process");

        (await client.GetAsync(HealthEndpoints.ReadyPath)).EnsureSuccessStatusCode();
        (await client.GetAsync(HealthEndpoints.ReadyPath)).EnsureSuccessStatusCode();

        probe.Calls.Should().Be(2,
            "readiness has to ask on every request — a cached answer would keep routing traffic to an "
            + "instance whose store went down after the first probe");
    }

    // The global limiter is 100 requests a minute per partition, and every TestServer request lands in
    // the same "unknown" partition because Connection.RemoteIpAddress is never populated — so 120
    // requests through one client is a genuine test of the exemption rather than a formality. Without
    // DisableRateLimiting the 101st answers 429, which is a probe reporting the app as DOWN because the
    // app is BUSY: precisely backwards, and worst under load.
    [Theory]
    [InlineData(HealthEndpoints.LivePath)]
    [InlineData(HealthEndpoints.ReadyPath)]
    public async Task AProbe_IsNotRateLimited(string path)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        for (var i = 0; i < 120; i++)
        {
            var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} must not be throttled", i + 1);
        }
    }

    // MapHealthChecks maps EVERY method by default. Constraining both probes to GET is what keeps them
    // out of CsrfGuardMiddleware's reach entirely: that middleware validates POST/PUT/DELETE/PATCH, so
    // a route with no unsafe method has nothing to exempt. Requested WITH a bearer credential, because
    // the 405 endpoint carries no authorization metadata and the fallback policy would challenge an
    // anonymous caller first — a 401 here would be evidence of authentication, not of the method
    // constraint.
    [Theory]
    [InlineData(HealthEndpoints.LivePath)]
    [InlineData(HealthEndpoints.ReadyPath)]
    public async Task AProbe_RefusesAnUnsafeMethod(string path)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, path).WithBearer(token);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    private sealed class UnreachableProbe : IPersistenceProbe
    {
        public Task<bool> CanReachAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class CountingProbe : IPersistenceProbe
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> CanReachAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(true);
        }
    }
}

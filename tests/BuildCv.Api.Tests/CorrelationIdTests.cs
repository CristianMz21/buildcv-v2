using System.Net;
using System.Net.Http.Json;
using BuildCv.Api.Observability;
using BuildCv.Application.Common.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BuildCv.Api.Tests;

public sealed class CorrelationIdTests
{
    [Fact]
    public async Task ARequestThatSendsNoCorrelationId_IsGivenOne()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync(HealthEndpointsPath);

        Echoed(response).Should().MatchRegex("^[0-9a-f]{32}$",
            "a generated id is a GUID in N format, which is inside the safe character set by construction");
    }

    [Fact]
    public async Task TwoRequests_AreGivenDifferentCorrelationIds()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var first = Echoed(await client.GetAsync(HealthEndpointsPath));
        var second = Echoed(await client.GetAsync(HealthEndpointsPath));

        first.Should().NotBe(second, "an id shared by every request correlates nothing");
    }

    // The proxy case the header exists for: something upstream already minted an id, and this API has to
    // adopt it rather than start a second one nothing can join to.
    [Theory]
    [InlineData("0af7651916cd43dd8448eb211c80319c")]
    [InlineData("7b6e0d5a-2ac1-4a4f-9d5c-6f0b1f2a3c4d")]
    [InlineData("edge-1")]
    public async Task AnInboundCorrelationIdThatIsSafeToLog_IsAdopted(string inbound)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, HealthEndpointsPath);
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, inbound);

        Echoed(await client.SendAsync(request)).Should().Be(inbound);
    }

    // The value reaches log output, so this is the log-injection boundary. Every case here is a string
    // that is legal in an HTTP header and hostile in a log line: separators and quotes that can forge a
    // second field inside a structured entry, and a length that can bury a real entry under one request.
    //
    // Asserted as "replaced with a generated id", not merely "not echoed verbatim": trimming or
    // stripping would answer the same NotBe assertion while reporting an id the caller never sent
    // beside one that looks like the one they did.
    [Theory]
    [InlineData("has space")]
    [InlineData("quote\"inside")]
    [InlineData("brace{CorrelationId}")]
    [InlineData("semi;colon")]
    [InlineData("comma,separated")]
    [InlineData("equals=sign")]
    [InlineData("tab\tinside")]
    public async Task AnInboundCorrelationIdThatIsNotSafeToLog_IsReplaced(string inbound)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, HealthEndpointsPath);
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, inbound);

        var echoed = Echoed(await client.SendAsync(request));

        echoed.Should().MatchRegex("^[0-9a-f]{32}$");
        echoed.Should().NotContain(inbound);
    }

    [Fact]
    public async Task AnInboundCorrelationIdLongerThanTheCeiling_IsReplaced()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var atTheCeiling = new string('a', CorrelationIdMiddleware.MaxLength);
        var overIt = new string('a', CorrelationIdMiddleware.MaxLength + 1);

        using var accepted = new HttpRequestMessage(HttpMethod.Get, HealthEndpointsPath);
        accepted.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, atTheCeiling);
        Echoed(await client.SendAsync(accepted)).Should().Be(atTheCeiling,
            "the boundary is closed from both sides, or the ceiling could be anywhere below it");

        using var refused = new HttpRequestMessage(HttpMethod.Get, HealthEndpointsPath);
        refused.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, overIt);
        Echoed(await client.SendAsync(refused)).Should().MatchRegex("^[0-9a-f]{32}$");
    }

    // StringValues.ToString() joins repeats with a comma, which is outside the safe set — so this lands
    // in the replacement branch rather than silently picking one of the two.
    [Fact]
    public async Task ARequestThatSendsTheHeaderTwice_IsGivenAGeneratedId()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, HealthEndpointsPath);
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, "first");
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, "second");

        var echoed = Echoed(await client.SendAsync(request));

        echoed.Should().MatchRegex("^[0-9a-f]{32}$");
        echoed.Should().NotBe("first").And.NotBe("second");
    }

    // THE HALF THAT MAKES THE ID WORTH ANYTHING. An id echoed to the caller and absent from the logs is
    // a header, not a correlation.
    //
    // TWO requests, and each one's lines are checked for its OWN id and against the OTHER's. That pair
    // is what makes the assertion falsifiable: an id attached once and never cleared — a static field,
    // a scope opened at startup — would satisfy "every line carries an id" and satisfy it with the
    // wrong one. Only the cross-check can tell a per-request scope from an ambient string.
    //
    // The set checked is this application's own categories (BuildCv.*, plus the Program category the
    // endpoint loggers use) AND the framework's endpoint middleware, which runs downstream of this
    // middleware and therefore inherits the scope — evidence that what is being attached is a real
    // ILogger scope rather than something only this repo's own logging calls happen to include.
    //
    // Hosting's "Request starting/finished" lines are deliberately outside that set: HostingApplication
    // wraps the whole pipeline from OUTSIDE, so no middleware can put a scope on them.
    [Fact]
    public async Task EveryLineARequestWrites_CarriesThatRequestsCorrelationId()
    {
        var recorder = new RecordingLoggerProvider();
        using var factory = new ApiTestFactory(configureServices: RecordingLogging.Capturing(recorder));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // Failed logins, because they reliably log: AuditLog writes login_failure. Two of them fit
        // inside the 5/min auth window a TestServer client gets.
        var start = recorder.Records.Count;
        var first = Echoed(await client.PostAsJsonAsync(
            "/v1/auth/login", new { email = "nobody@example.com", password = TestHelpers.Password }));
        var middle = recorder.Records.Count;
        var second = Echoed(await client.PostAsJsonAsync(
            "/v1/auth/login", new { email = "nobody-else@example.com", password = TestHelpers.Password }));
        var end = recorder.Records.Count;

        first.Should().NotBe(second);

        AssertScopedTo(recorder.Records.Skip(start).Take(middle - start).ToList(), first, second);
        AssertScopedTo(recorder.Records.Skip(middle).Take(end - middle).ToList(), second, first);
    }

    private static void AssertScopedTo(
        IReadOnlyList<RecordedLog> written, string ownId, string otherRequestsId)
    {
        var scoped = written.Where(IsScopeBearing).ToList();

        scoped.Should().NotBeEmpty("the request has to have logged something, or the rest is vacuous");
        scoped.Should().Contain(record => record.Message.Contains("login_failure", StringComparison.Ordinal),
            "the audit line is the one this test is built around");
        scoped.Should().Contain(record => record.Category.Contains("EndpointMiddleware", StringComparison.Ordinal),
            "a framework line from inside the pipeline is what shows this is a real ILogger scope");

        scoped.Should().OnlyContain(record => record.ScopeValues.Contains(ownId));
        scoped.Should().NotContain(record => record.ScopeValues.Contains(otherRequestsId));
    }

    // The same header on the response class it is most needed for. ExceptionHandlerMiddleware clears the
    // response before the IExceptionHandlers run, so an eagerly assigned header is gone by the time this
    // body is written — measured as issue #22 for the security headers, and the reason the echo goes
    // through OnStarting. The 500 is forced the same way SecurityHeadersTests forces it.
    [Fact]
    public async Task AnExceptionHandledResponse_StillCarriesTheCorrelationId_AndSoDoesItsLogLine()
    {
        var recorder = new RecordingLoggerProvider();
        using var factory = new ApiTestFactory(configureServices: services =>
        {
            RecordingLogging.Capturing(recorder)(services);
            services.RemoveAll<IPasswordHasher>();
            services.AddSingleton<IPasswordHasher, ThrowingPasswordHasher>();
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var before = recorder.Records.Count;

        var response = await client.PostAsJsonAsync(
            "/v1/auth/register",
            new { email = TestHelpers.CandidateEmail, password = TestHelpers.Password, role = (string?)null });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var correlationId = Echoed(response);
        correlationId.Should().MatchRegex("^[0-9a-f]{32}$");

        recorder.Records.Skip(before)
            .Where(record => record.Category.Contains("GlobalExceptionHandler", StringComparison.Ordinal))
            .Should().NotBeEmpty()
            .And.OnlyContain(record => record.ScopeValues.Contains(correlationId),
                "the stack trace in the log and the 500 the caller saw have to name the same request");
    }

    // Any endpoint would do; this one is anonymous and rate-limit exempt, so a theory can hammer it
    // without spending an auth window.
    private const string HealthEndpointsPath = "/health/live";

    // This application's own loggers, plus the one framework logger that provably sits downstream of
    // CorrelationIdMiddleware because endpoints are mapped after every Use call in Program.cs.
    private static bool IsScopeBearing(RecordedLog record) =>
        record.Category.StartsWith("BuildCv", StringComparison.Ordinal)
        || record.Category == "Program"
        || record.Category.Contains("EndpointMiddleware", StringComparison.Ordinal);

    private static string Echoed(HttpResponseMessage response) =>
        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();

    private sealed class ThrowingPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => throw new InvalidOperationException("Hashing is unavailable.");

        public bool Verify(string password, string hashedPassword) => throw new InvalidOperationException();
    }
}

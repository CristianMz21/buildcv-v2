using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Application.Common.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BuildCv.Api.Tests;

public sealed class SecurityHeadersTests
{
    [Fact]
    public async Task Responses_IncludeSecurityHeaders()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/auth/me");

        AssertTheFullSet(response);
    }

    // THE PATH THAT HAD NONE OF THEM, and the reason every test above it passed anyway: they all run on
    // success paths. SecurityHeadersMiddleware used to write its headers on the way in, and it sits
    // immediately outside UseExceptionHandler, which CLEARS the response before invoking the
    // IExceptionHandlers — so the headers were present on every response except the ones carrying an
    // exception's message. That is issue #22.
    //
    // The 500 is forced by swapping in a hasher that throws, which is the only way to reach
    // GlobalExceptionHandler from a test: nothing in the in-memory store fails, and every DomainException
    // is already turned into a Result<T> by its handler. RegisterAccountHandler catches DomainException
    // and ArgumentException only, so an InvalidOperationException from Hash goes straight up.
    //
    // The status alone would not identify the mechanism, so the body is checked too: a 500 that did NOT
    // come from GlobalExceptionHandler would be a different response produced by a different path, and
    // could carry a different header set for reasons this test is not about.
    //
    // It is asserted on the ProblemDetails `title` rather than on the content type, because the content
    // type here is application/json and not application/problem+json — measured. Three of the four
    // handlers call WriteAsJsonAsync(ProblemDetails) with no explicit content type; only
    // MalformedRequestExceptionHandler passes one. The BODY is ProblemDetails-shaped in all four.
    [Fact]
    public async Task AnExceptionHandledResponse_StillCarriesEverySecurityHeader()
    {
        using var factory = new ApiTestFactory(configureServices: services =>
        {
            services.RemoveAll<IPasswordHasher>();
            services.AddSingleton<IPasswordHasher, ThrowingPasswordHasher>();
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/auth/register",
            new { email = "candidate@example.com", password = TestHelpers.Password, role = (string?)null });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().Be("Internal Server Error",
            "the response has to be the one GlobalExceptionHandler produced, not an escape");

        AssertTheFullSet(response);
    }

    // THE ONE HEADER THE MIDDLEWARE CANNOT REACH. Kestrel writes `Server: Kestrel` from its own defaults
    // after the OnStarting callbacks have run, so removing the header from the response collection there
    // achieves nothing — measured on a real Kestrel host, in both the eager and the callback shape. The
    // only lever is AddServerHeader, and until now nothing pinned it: deleting that line from Program.cs
    // reddened no test, while the NotContain assertion above looked as though it covered the case.
    //
    // Asserted against the real application's options rather than over a socket, which is the same split
    // ResumeImportSizeLimitTests uses for the request-size ceiling: the app DECLARES, the server
    // ENFORCES, and only the first half is this repository's to state.
    [Fact]
    public void TheHostSuppressesKestrelsServerHeader()
    {
        using var factory = new ApiTestFactory();

        factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>()
            .Value.AddServerHeader.Should().BeFalse();
    }

    // Stated once so the two callers cannot drift, and stated in FULL on purpose: the bug this file now
    // covers dropped all seven at once, so a test asserting a representative one or two would have
    // reported the same green for a fix that only reinstated the ones it happened to name.
    private static void AssertTheFullSet(HttpResponseMessage response)
    {
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
        response.Headers.GetValues("Content-Security-Policy")
            .Should().Contain("default-src 'none'; frame-ancestors 'none'");
        response.Headers.GetValues("Permissions-Policy")
            .Should().Contain("camera=(), microphone=(), geolocation=()");
        response.Headers.GetValues("Cross-Origin-Opener-Policy").Should().Contain("same-origin");
        response.Headers.GetValues("Cross-Origin-Resource-Policy").Should().Contain("same-origin");

        // TestServer never sets a Server header, so this line passes whether or not anything removes one
        // — it is a regression guard against something in the pipeline ADDING one, and it is NOT evidence
        // about production. TheHostSuppressesKestrelsServerHeader below is where that lives.
        response.Headers.Should().NotContain(h => h.Key == "Server");
    }

    private sealed class ThrowingPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => throw new InvalidOperationException("Hashing is unavailable.");

        public bool Verify(string password, string hashedPassword) => throw new InvalidOperationException();
    }
}

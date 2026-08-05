using FluentAssertions;

namespace BuildCv.Api.Tests;

public sealed class SecurityHeadersTests
{
    [Fact]
    public async Task Responses_IncludeSecurityHeaders()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/auth/me");

        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
        response.Headers.GetValues("Content-Security-Policy")
            .Should().Contain("default-src 'none'; frame-ancestors 'none'");
        response.Headers.GetValues("Permissions-Policy")
            .Should().Contain("camera=(), microphone=(), geolocation=()");
        response.Headers.GetValues("Cross-Origin-Opener-Policy").Should().Contain("same-origin");
        response.Headers.GetValues("Cross-Origin-Resource-Policy").Should().Contain("same-origin");
        response.Headers.Should().NotContain(h => h.Key == "Server");
    }
}

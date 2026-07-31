using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

public sealed class AuthFlowTests
{
    [Fact]
    public async Task Register_Then_Login_ReturnsTokensAsSecureCookies()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var register = await client.RegisterAsync(TestHelpers.CandidateEmail);
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        var login = await client.LoginAsync(TestHelpers.CandidateEmail);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        // Cookie attribute names are case-insensitive (RFC 6265 5.2) and ASP.NET Core serializes
        // them lowercase, so these assertions must ignore case.
        var accessCookie = TestHelpers.GetSetCookie(login, "access_token");
        accessCookie.Should().ContainEquivalentOf("HttpOnly").And.ContainEquivalentOf("SameSite=Strict");

        var refreshCookie = TestHelpers.GetSetCookie(login, "refresh_token");
        refreshCookie.Should().ContainEquivalentOf("HttpOnly").And.ContainEquivalentOf("SameSite=Strict")
            .And.ContainEquivalentOf("Path=/auth/refresh");
    }

    // Anonymous callers must not be able to name a privileged role at registration. Each case gets
    // its own factory so the shared 5/min auth rate limit never masks a failure.
    [Theory]
    [InlineData("Admin")]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData("aDmIn")]
    [InlineData("2")]
    public async Task Register_WithAdminRole_IsRejectedAndCreatesNoAccount(string role)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var register = await client.RegisterAsync("escalate@example.com", role);

        register.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        register.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var login = await client.LoginAsync("escalate@example.com");
        login.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Candidate")]
    [InlineData("Recruiter")]
    [InlineData("recruiter")]
    public async Task Register_WithSelfAssignableRole_Succeeds(string role)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var register = await client.RegisterAsync(TestHelpers.CandidateEmail, role);

        register.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsGenericError()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/auth/login",
            new { email = TestHelpers.CandidateEmail, password = "wrong-password" });

        ((int)login.StatusCode).Should().BeOneOf(400, 401);
        var body = await login.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid credentials.");
        body.Should().NotContain("password");
    }

    [Fact]
    public async Task Me_WithAccessTokenCookie_Returns200_Without_Returns401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();

        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var me = await client.GetAsync("/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        using var anonymous = factory.CreateClient();
        var unauthorized = await anonymous.GetAsync("/auth/me");
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_WithBearerHeader_Returns200()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me").WithBearer(token);
        var me = await client.SendAsync(request);

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await me.Content.ReadAsStringAsync();
        body.Should().Contain(TestHelpers.CandidateEmail);
    }
}

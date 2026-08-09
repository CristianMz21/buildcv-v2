using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

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
            .And.ContainEquivalentOf("Path=/v1/auth/refresh");
    }

    // THE LOCATION HEADER IS FOLLOWED, not merely compared to a string. It used to name
    // /v1/auth/accounts/{id}, which is mapped nowhere, so the standard "201 then GET the Location"
    // convention answered 404 — and no test noticed, because nothing followed it.
    //
    // BOTH HALVES ARE NEEDED, and neither alone would have caught it. Asserting the header's VALUE
    // pins today's answer without saying whether that path resolves; asserting only that a GET of it
    // succeeds would pass on any route this suite happened to reach. Following the header the API
    // really sent, with a credential, is the assertion the convention actually makes.
    //
    // THE ID IS COMPARED, because /v1/auth/me answers 200 for whatever principal calls it: a Location
    // pointing at some other account's resource would return a body of the same shape and status. The
    // id the 201 carried is the only thing that says the header named the resource it created.
    //
    // Two auth requests — a register and a login — out of the 5/min a TestServer client gets.
    [Fact]
    public async Task Register_TheLocationItReturns_ResolvesToTheAccountItJustCreated()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var register = await client.RegisterAsync(TestHelpers.CandidateEmail);
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        var location = register.Headers.Location;
        location.Should().NotBeNull("a 201 has to say where the thing it created lives");

        var created = await register.Content.ReadFromJsonAsync<AccountBody>();

        // Registering does not log you in, so the header is followed with a credential obtained the
        // way a client would obtain one. Without it this route answers 401 — a real refusal from a
        // route that exists, which is the accepted cost stated on the endpoint.
        var token = await client.LoginAndGetAccessTokenAsync(TestHelpers.CandidateEmail);

        using var follow = new HttpRequestMessage(HttpMethod.Get, location).WithBearer(token);
        var followed = await client.SendAsync(follow);

        followed.StatusCode.Should().Be(HttpStatusCode.OK,
            "following a 201's Location must not 404 — {0} is the path the API sent", location);

        var read = await followed.Content.ReadFromJsonAsync<AccountBody>();
        read!.Id.Should().Be(created!.Id, "the Location must name the account the 201 reported creating");
    }

    private sealed record AccountBody(Guid Id);

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

    // Its own factory for the same reason as the test above: the shared 5/min auth window would
    // otherwise turn a real failure into a 429 and read as "rejected" either way.
    [Theory]
    [InlineData("a")]
    [InlineData("hunter2")]
    [InlineData("11character")]
    [InlineData("")]
    public async Task Register_WithAWeakPassword_IsRejectedAndCreatesNoAccount(string weak)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync(
            "/v1/auth/register", new { email = "weak@example.com", password = weak });

        register.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        register.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var login = await client.PostAsJsonAsync(
            "/v1/auth/login", new { email = "weak@example.com", password = weak });
        login.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_WithAWeakPassword_DoesNotEchoItIntoTheResponseBody()
    {
        // The refusal must not put the credential into a body that ends up in a browser console,
        // a proxy log or an error tracker. The sentinel is distinctive on purpose: asserting this
        // against a one-character password would fail on the word "at" in the message itself and
        // prove nothing about echoing.
        const string weak = "zzqqxxjj";

        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync(
            "/v1/auth/register", new { email = "weak-echo@example.com", password = weak });

        register.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await register.Content.ReadAsStringAsync()).Should().NotContain(weak);
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
    public async Task Login_OutsideDevelopment_MarksAuthCookiesSecure()
    {
        using var factory = new ApiTestFactory(Environments.Staging);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false
        });

        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();
        var login = await client.LoginAsync(TestHelpers.CandidateEmail);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        TestHelpers.HasCookieAttribute(login, "access_token", "Secure").Should().BeTrue();
        TestHelpers.HasCookieAttribute(login, "refresh_token", "Secure").Should().BeTrue();
    }

    [Fact]
    public async Task Login_InDevelopment_OmitsSecureSoLocalHttpKeepsWorking()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();
        var login = await client.LoginAsync(TestHelpers.CandidateEmail);

        TestHelpers.HasCookieAttribute(login, "access_token", "Secure").Should().BeFalse();
        TestHelpers.HasCookieAttribute(login, "refresh_token", "Secure").Should().BeFalse();
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsGenericError()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/v1/auth/login",
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

        var me = await client.GetAsync("/v1/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        using var anonymous = factory.CreateClient();
        var unauthorized = await anonymous.GetAsync("/v1/auth/me");
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_WithBearerHeader_Returns200()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/me").WithBearer(token);
        var me = await client.SendAsync(request);

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await me.Content.ReadAsStringAsync();
        body.Should().Contain(TestHelpers.CandidateEmail);
    }
}

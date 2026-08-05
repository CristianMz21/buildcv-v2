using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// The /v1 prefix is a claim about the whole route table, so it is pinned from both sides: the
// versioned path answers AND the unversioned one reaches no handler — 404 to an authenticated
// caller, 401 to an anonymous one (see the first test for why they differ). A lone not-found
// assertion passes just as happily against a typo'd path as against a versioned API, which would
// make it a control that proves nothing.
public sealed class VersioningTests
{
    // The anonymous half of the boundary. An unmatched path does NOT answer 404 to an anonymous
    // caller: the fallback authorization policy is consulted even when routing matched no endpoint
    // (AuthorizationPolicy.CombineAsync falls back whenever there is no endpoint authorization
    // metadata, and a nonexistent endpoint has none), so the request is challenged first. Measured:
    // the 401 carries the "about:blank" ProblemDetails of a policy rejection, not the login
    // handler's shape — that handler answers 400 on bad credentials and can produce no 401. So the
    // old path is dead in both directions, it just fails closed for anonymous callers.
    [Fact]
    public async Task Login_AnswersUnderV1_AndTheUnversionedPathAnswersNothing()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();

        var versioned = await client.PostAsJsonAsync("/v1/auth/login",
            new { email = TestHelpers.CandidateEmail, password = TestHelpers.Password });
        versioned.StatusCode.Should().Be(HttpStatusCode.OK,
            "the versioned route must answer, or the refusal below is evidence of a dead API rather than of versioning");

        var unversioned = await client.PostAsJsonAsync("/auth/login",
            new { email = TestHelpers.CandidateEmail, password = TestHelpers.Password });
        unversioned.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the same valid credentials on the unversioned path must reach no login handler");
        unversioned.Headers.Contains("Set-Cookie").Should().BeFalse(
            "a path that mints tokens for a request it never authenticated would be a routing hole, not a 401");
    }

    // One representative unversioned route per group, requested WITH a valid credential so the 404
    // cannot be an authentication refusal in disguise. 404 rather than 401 or 403 is also the correct
    // behaviour, not a shortcut: an unmatched path has no endpoint, so the fallback authorization
    // policy never runs.
    [Theory]
    [InlineData("GET", "/auth/me")]
    [InlineData("GET", "/resumes")]
    [InlineData("GET", "/jobs/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/job-offers/extract")]
    [InlineData("GET", "/organizations/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/scoring/00000000-0000-0000-0000-000000000001")]
    public async Task EveryUnversionedGroupPath_Answers404(string method, string path)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(new HttpMethod(method), path).WithBearer(token);
        if (method == "POST")
            request.Content = JsonContent.Create(new { text = "irrelevant" });

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // The refresh cookie is path-scoped to the endpoint it feeds. If AuthCookies.RefreshCookiePath
    // does not move with the route, the browser never presents the refresh token again and every
    // session dies at access-token expiry — silently, because a missing cookie 401s like an idle user.
    [Fact]
    public async Task Login_ScopesTheRefreshCookieToTheV1RefreshPath()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();

        var login = await client.LoginAsync(TestHelpers.CandidateEmail);

        login.EnsureSuccessStatusCode();
        TestHelpers.HasCookieAttribute(login, "refresh_token", "path=/v1/auth/refresh").Should().BeTrue(
            "a refresh cookie scoped to any other path is never sent to the refresh endpoint");
    }

    // CsrfGuardMiddleware.ExemptPaths must move with the routes, and this is the direction that fails
    // against users rather than attackers: with stale entries, a cookie-authenticated POST to
    // /v1/auth/refresh enters CSRF validation — as it holds the access-token cookie and no bearer
    // header — and 403s, so no browser session can ever renew. The attacker-facing direction, that
    // /v1/auth/logout STAYS guarded, is pinned by
    // SessionTerminationTests.Logout_CookieAuthenticatedWithoutCsrfToken_Returns403AndRevokesNothing.
    [Fact]
    public async Task Refresh_FromACookieClientWithoutACsrfToken_IsStillExempt()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var refresh = await client.PostAsync("/v1/auth/refresh", content: null);

        refresh.StatusCode.Should().Be(HttpStatusCode.OK,
            "an exempt path must not demand a CSRF header no client has fetched at refresh time");
    }
}

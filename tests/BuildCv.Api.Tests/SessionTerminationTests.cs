using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BuildCv.Api.Tests;

// Regression cover for the gap found in review: clearing the auth cookies disarmed the browser but
// left the opaque refresh token valid in the store, so a copy captured before logout still minted
// fresh access tokens at /auth/refresh for up to 30 days.
public sealed class SessionTerminationTests
{
    private const string NewPassword = "An0ther!Password#2026";

    private static HttpClient CreateRawClient(ApiTestFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    // Captures both halves of one login so the scenario spends a single auth rate-limit slot.
    private static async Task<(string AccessToken, string RefreshCookie)> RegisterAndCaptureSessionAsync(
        HttpClient client, string email, string? role = null)
    {
        (await client.RegisterAsync(email, role)).EnsureSuccessStatusCode();

        var login = await client.LoginAsync(email);
        login.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return (
            body.RootElement.GetProperty("accessToken").GetString()!,
            TestHelpers.GetCookieValue(login, "refresh_token"));
    }

    private static async Task<HttpResponseMessage> ReplayRefreshTokenAsync(HttpClient client, string refreshCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        request.Headers.Add("Cookie", refreshCookie);
        return await client.SendAsync(request);
    }

    private static async Task AssertReplayRejectedAsync(HttpClient client, string refreshCookie)
    {
        var replay = await ReplayRefreshTokenAsync(client, refreshCookie);

        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await replay.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be("Invalid refresh token.");
    }

    // Rebuilds the caller's own token with an expiry in the past, which is what an idle browser
    // actually presents. Signed with the real key unless `signingKey` says otherwise.
    private static string StaleTokenFrom(string liveAccessToken, string signingKey = ApiTestFactory.SigningKey)
    {
        var handler = new JsonWebTokenHandler();
        var live = handler.ReadJsonWebToken(liveAccessToken);
        var now = DateTime.UtcNow;

        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ApiTestFactory.Issuer,
            Audience = ApiTestFactory.Audience,
            IssuedAt = now.AddMinutes(-30),
            NotBefore = now.AddMinutes(-30),
            Expires = now.AddMinutes(-15),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = live.GetClaim("sub").Value,
                ["email"] = live.GetClaim("email").Value,
                ["role"] = live.GetClaim("role").Value,
                ["jti"] = Guid.NewGuid().ToString()
            }
        });
    }

    private static DateTimeOffset ExpiresOf(HttpResponseMessage response, string cookieName) =>
        DateTimeOffset.Parse(
            TestHelpers.GetSetCookie(response, cookieName)
                .Split(';')
                .Select(attribute => attribute.Trim())
                .First(attribute => attribute.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..],
            CultureInfo.InvariantCulture);

    private static HttpRequestMessage ChangePasswordRequest(string accessToken, string currentPassword) =>
        new HttpRequestMessage(HttpMethod.Post, "/auth/change-password")
        {
            Content = JsonContent.Create(new { currentPassword, newPassword = NewPassword })
        }.WithBearer(accessToken);

    [Fact]
    public async Task Logout_RevokesCapturedRefreshToken_ReplayAtRefreshIsRejected()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        var (accessToken, capturedRefreshCookie) =
            await RegisterAndCaptureSessionAsync(client, TestHelpers.CandidateEmail);

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/logout").WithBearer(accessToken);
        var logout = await client.SendAsync(logoutRequest);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertReplayRejectedAsync(client, capturedRefreshCookie);
    }

    [Fact]
    public async Task ChangePassword_RevokesCapturedRefreshToken_ReplayAtRefreshIsRejected()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        var (accessToken, capturedRefreshCookie) =
            await RegisterAndCaptureSessionAsync(client, TestHelpers.CandidateEmail);

        using var changeRequest = ChangePasswordRequest(accessToken, TestHelpers.Password);
        var change = await client.SendAsync(changeRequest);
        change.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertReplayRejectedAsync(client, capturedRefreshCookie);
    }

    // The caller's own cookies are dead weight once the handler revoked every token, so the
    // response has to tell the browser to drop them.
    [Fact]
    public async Task ChangePassword_ClearsCallersAuthCookies()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        var (accessToken, _) = await RegisterAndCaptureSessionAsync(client, TestHelpers.CandidateEmail);

        using var changeRequest = ChangePasswordRequest(accessToken, TestHelpers.Password);
        var change = await client.SendAsync(changeRequest);

        change.StatusCode.Should().Be(HttpStatusCode.OK);
        TestHelpers.GetCookieValue(change, "access_token").Should().Be("access_token=");
        TestHelpers.GetCookieValue(change, "refresh_token").Should().Be("refresh_token=");
    }

    // A failed password change must not end the caller's sessions: that would hand anyone holding
    // a stolen access token a one-request logout weapon against the real owner.
    [Fact]
    public async Task ChangePassword_WithWrongCurrentPassword_LeavesSessionAlive()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        var (accessToken, capturedRefreshCookie) =
            await RegisterAndCaptureSessionAsync(client, TestHelpers.CandidateEmail);

        using var changeRequest = ChangePasswordRequest(accessToken, "wrong-password");
        var change = await client.SendAsync(changeRequest);
        change.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var replay = await ReplayRefreshTokenAsync(client, capturedRefreshCookie);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // THE scenario logout exists for: the tab sat idle, the access token expired, the user presses
    // "log out". If the endpoint can only act on an unexpired token, this path revokes nothing and
    // the captured refresh cookie stays live for the rest of its 30 days — the original bug,
    // untouched. Two things have to hold for this to work: the access cookie has to outlive the
    // JWT (see Login_AccessCookie... below) and logout has to authenticate against a scheme that
    // ignores `exp` while still checking the signature.
    [Fact]
    public async Task Logout_WithExpiredAccessToken_StillRevokesTheCapturedRefreshToken()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        var (accessToken, capturedRefreshCookie) =
            await RegisterAndCaptureSessionAsync(client, TestHelpers.CandidateEmail);
        var expiredToken = StaleTokenFrom(accessToken);

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/logout").WithBearer(expiredToken);
        var logout = await client.SendAsync(logoutRequest);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertReplayRejectedAsync(client, capturedRefreshCookie);
    }

    // The lenient scheme is reachable from /auth/logout and nowhere else. If an expired token ever
    // opened a protected endpoint, ignoring `exp` would have bought a session extension instead of
    // a logout.
    [Fact]
    public async Task ExpiredAccessToken_IsStillRejectedByEveryOtherEndpoint()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        var (accessToken, _) = await RegisterAndCaptureSessionAsync(client, TestHelpers.CandidateEmail);
        var expiredToken = StaleTokenFrom(accessToken);

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/me").WithBearer(expiredToken);
        var me = await client.SendAsync(meRequest);

        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Ignoring `exp` is not ignoring the signature. A token this API never issued must revoke
    // nothing, or logout becomes a way to end any account's sessions from anywhere.
    [Fact]
    public async Task Logout_WithForgedToken_RevokesNothing()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        var (accessToken, capturedRefreshCookie) =
            await RegisterAndCaptureSessionAsync(client, TestHelpers.CandidateEmail);
        var forgedToken = StaleTokenFrom(accessToken, signingKey: "attacker-signing-key-min-32-characters-long");

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/logout").WithBearer(forgedToken);
        (await client.SendAsync(logoutRequest)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var replay = await ReplayRefreshTokenAsync(client, capturedRefreshCookie);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // The access cookie used to carry Expires = now + AccessTokenMinutes, so the browser deleted
    // the only credential naming the account at the same moment the JWT went stale. The JWT's own
    // `exp` is the security control; the cookie lifetime is client-side housekeeping and belongs
    // to the session.
    [Fact]
    public async Task Login_AccessCookieOutlivesTheAccessTokenAndMatchesTheSession()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();
        var login = await client.LoginAsync(TestHelpers.CandidateEmail);
        login.EnsureSuccessStatusCode();

        var accessExpires = ExpiresOf(login, "access_token");

        accessExpires.Should().Be(ExpiresOf(login, "refresh_token"));
        accessExpires.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(1));
    }

    // With no credential at all there is nothing to identify, so nothing is revoked — but the
    // cookies still get cleared and the answer is still 204. The idle-token case is handled by
    // Logout_WithExpiredAccessToken_StillRevokesTheCapturedRefreshToken above; this is only the
    // genuinely anonymous caller.
    [Fact]
    public async Task Logout_WithoutCredentials_ClearsCookiesAndReturns204()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        var logout = await client.PostAsync("/auth/logout", content: null);

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        TestHelpers.GetCookieValue(logout, "access_token").Should().Be("access_token=");
        TestHelpers.GetCookieValue(logout, "refresh_token").Should().Be("refresh_token=");
    }

    // AllowAnonymous must not turn logout into a cross-site session-revocation gadget: the CSRF
    // guard still covers the route because it is not in CsrfGuardMiddleware.ExemptPaths.
    [Fact]
    public async Task Logout_CookieAuthenticatedWithoutCsrfToken_Returns403AndRevokesNothing()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();
        var login = await client.LoginAsync(TestHelpers.CandidateEmail);
        login.EnsureSuccessStatusCode();
        var accessCookie = TestHelpers.GetCookieValue(login, "access_token");
        var capturedRefreshCookie = TestHelpers.GetCookieValue(login, "refresh_token");

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        logoutRequest.Headers.Add("Cookie", accessCookie);
        var logout = await client.SendAsync(logoutRequest);

        logout.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var replay = await ReplayRefreshTokenAsync(client, capturedRefreshCookie);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_DoesNotRevokeOtherAccountsSessions()
    {
        using var factory = new ApiTestFactory();
        using var client = CreateRawClient(factory);

        var (_, bystanderRefreshCookie) =
            await RegisterAndCaptureSessionAsync(client, TestHelpers.RecruiterEmail, role: "Recruiter");
        var (accessToken, _) = await RegisterAndCaptureSessionAsync(client, TestHelpers.CandidateEmail);

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/logout").WithBearer(accessToken);
        (await client.SendAsync(logoutRequest)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var replay = await ReplayRefreshTokenAsync(client, bystanderRefreshCookie);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

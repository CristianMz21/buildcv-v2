using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

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

    // Requiring authentication on logout would answer 401 as soon as the access token expired,
    // stranding the user with cookies they cannot clear. An anonymous logout is a no-op that still
    // clears them and always answers 204.
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

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

public sealed class RefreshTokenTests
{
    [Fact]
    public async Task Refresh_RotatesTokens_OldRefreshTokenIsRejected()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();
        var login = await client.LoginAsync(TestHelpers.CandidateEmail);
        login.EnsureSuccessStatusCode();
        var oldRefreshCookie = TestHelpers.GetCookieValue(login, "refresh_token");

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        refreshRequest.Headers.Add("Cookie", oldRefreshCookie);
        var refresh = await client.SendAsync(refreshRequest);

        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        TestHelpers.GetSetCookie(refresh, "access_token");
        TestHelpers.GetSetCookie(refresh, "refresh_token");

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        replayRequest.Headers.Add("Cookie", oldRefreshCookie);
        var replay = await client.SendAsync(replayRequest);

        ((int)replay.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task Refresh_WithoutCookie_Returns401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsync("/auth/refresh", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

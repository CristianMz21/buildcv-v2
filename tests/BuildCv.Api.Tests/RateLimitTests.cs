using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

public sealed class RateLimitTests
{
    [Fact]
    public async Task SixthLoginAttemptWithinWindow_Returns429WithRetryAfter()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();

        // Register consumes 1 of the 5 auth-window slots; 4 logins pass, the rest are rejected.
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 6; i++)
        {
            responses.Add(await client.PostAsJsonAsync("/auth/login",
                new { email = TestHelpers.CandidateEmail, password = "wrong-password" }));
        }

        responses.Take(4).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.BadRequest);
        responses.Skip(4).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        responses.Skip(4).First().Headers.Should().Contain(h => h.Key == "Retry-After");
    }

    // /auth/change-password verifies the current password, so it shares the auth window with
    // register/login/refresh instead of running at the global 100/min.
    [Fact]
    public async Task RepeatedWrongCurrentPasswordChangeAttempts_Return429WithRetryAfter()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        // Register and login already consumed 2 of the 5 auth-window slots for this partition.
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/change-password")
            {
                Content = JsonContent.Create(new
                {
                    currentPassword = "wrong-password",
                    newPassword = "An0ther!Password#2026"
                })
            }.WithBearer(token);
            responses.Add(await client.SendAsync(request));
        }

        responses.Take(3).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.BadRequest);
        responses[3].StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        responses[3].Headers.Should().Contain(h => h.Key == "Retry-After");
    }
}

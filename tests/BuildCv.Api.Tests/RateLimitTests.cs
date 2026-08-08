using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

public sealed class RateLimitTests
{
    private const string NewPassword = "An0ther!Password#2026";

    private static HttpRequestMessage ChangePasswordRequest(string accessToken, string currentPassword) =>
        new HttpRequestMessage(HttpMethod.Post, "/v1/auth/change-password")
        {
            Content = JsonContent.Create(new { currentPassword, newPassword = NewPassword })
        }.WithBearer(accessToken);

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
            responses.Add(await client.PostAsJsonAsync("/v1/auth/login",
                new { email = TestHelpers.CandidateEmail, password = "wrong-password" }));
        }

        responses.Take(4).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.BadRequest);
        responses.Skip(4).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        responses.Skip(4).First().Headers.Should().Contain(h => h.Key == "Retry-After");
    }

    // End-to-end sanity check that no forwarded-headers middleware is in the pipeline by default,
    // so a client-supplied X-Forwarded-For cannot reach a partition key. Note this would also pass
    // against the unfixed code: TestServer never populates Connection.RemoteIpAddress, so every
    // request lands in the shared "unknown" bucket regardless of headers. It guards the wiring —
    // that nobody adds an unconditional UseForwardedHeaders — not the key derivation.
    // RateLimitPartitionTests is the load-bearing coverage for who is allowed to set that header.
    [Fact]
    public async Task SpoofedForwardedForHeader_DoesNotBuyExtraAuthAttempts()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 6; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login")
            {
                Content = JsonContent.Create(new { email = "nobody@example.com", password = "wrong-password" })
            };
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            responses.Add(await client.SendAsync(request));
        }

        responses.Take(5).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.BadRequest);
        responses[5].StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    // /auth/change-password is throttled per account, not through the per-IP auth window: register
    // and login no longer eat into its budget, so all 5 attempts are available.
    [Fact]
    public async Task SixthWrongCurrentPasswordChangeAttempt_Returns429WithRetryAfter()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 6; i++)
        {
            using var request = ChangePasswordRequest(token, "wrong-password");
            responses.Add(await client.SendAsync(request));
        }

        responses.Take(5).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.BadRequest);
        responses[5].StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        responses[5].Headers.Should().Contain(h => h.Key == "Retry-After");
    }

    // /auth/logout is anonymous and state-changing, and each authenticated hit scans the token
    // store, so it needs its own ceiling — authentication used to be its de facto throttle.
    [Fact]
    public async Task LogoutBeyondItsOwnWindow_Returns429WithRetryAfter()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 21; i++)
            responses.Add(await client.PostAsync("/v1/auth/logout", content: null));

        responses.Take(20).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.NoContent);
        responses[20].StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        responses[20].Headers.Should().Contain(h => h.Key == "Retry-After");
    }

    // Logout has its own window precisely so a logout storm cannot starve the login budget for
    // everyone on a shared address.
    [Fact]
    public async Task LogoutTraffic_DoesNotConsumeTheAuthWindow()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        for (var i = 0; i < 10; i++)
            (await client.PostAsync("/v1/auth/logout", content: null)).Dispose();

        var register = await client.RegisterAsync(TestHelpers.CandidateEmail);

        register.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // The NAT case: two accounts reaching the API from one address. Exhausting one account's
    // password-change budget must not deny password rotation to the other.
    [Fact]
    public async Task ExhaustedPasswordChangeBudget_DoesNotBlockAnotherAccountOnTheSameAddress()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, noisyToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, neighbourToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        for (var i = 0; i < 6; i++)
        {
            using var request = ChangePasswordRequest(noisyToken, "wrong-password");
            (await client.SendAsync(request)).Dispose();
        }

        using var neighbourRequest = ChangePasswordRequest(neighbourToken, TestHelpers.Password);
        var neighbour = await client.SendAsync(neighbourRequest);

        neighbour.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

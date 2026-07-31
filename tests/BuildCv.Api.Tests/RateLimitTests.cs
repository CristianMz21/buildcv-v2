using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

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
}

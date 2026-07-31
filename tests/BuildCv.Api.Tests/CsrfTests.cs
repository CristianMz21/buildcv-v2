using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

public sealed class CsrfTests
{
    private static readonly object ResumeBody = new
    {
        fullName = "Jane Candidate",
        email = "jane@example.com",
        phoneNumber = (string?)null,
        location = (string?)null,
        summary = (string?)null
    };

    [Fact]
    public async Task CookieMutation_WithoutCsrfToken_Returns403()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var login = await client.LoginAsync(TestHelpers.CandidateEmail);
        var accessCookie = TestHelpers.GetCookieValue(login, "access_token");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes")
        {
            Content = JsonContent.Create(ResumeBody)
        };
        request.Headers.Add("Cookie", accessCookie);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BearerMutation_WithoutCsrfToken_Succeeds()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes")
        {
            Content = JsonContent.Create(ResumeBody)
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}

using System.Net;
using System.Net.Http.Json;
using BuildCv.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
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

    // A blank Authorization header carries no bearer credential, so the JwtBearer handler still
    // authenticates from the cookie. The guard must not treat the bare header key as an exemption.
    // HttpClient drops a zero-length header value before it reaches TestServer, so whitespace is
    // the closest expressible equivalent here — the JwtBearer handler's IsNullOrWhiteSpace check
    // treats both identically, and this middleware must too.
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   ")]
    public async Task CookieMutation_WithBlankAuthorizationHeader_Returns403(string authorization)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        (await client.RegisterAsync(TestHelpers.CandidateEmail)).EnsureSuccessStatusCode();
        var login = await client.LoginAsync(TestHelpers.CandidateEmail);
        var accessCookie = TestHelpers.GetCookieValue(login, "access_token");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes")
        {
            Content = JsonContent.Create(ResumeBody)
        };
        request.Headers.Add("Cookie", accessCookie);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be("CSRF validation failed.");
    }

    [Fact]
    public async Task CookieMutation_WithAntiforgeryTokenFetchedAfterLogin_Succeeds()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();

        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var csrfToken = await client.GetAntiforgeryTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes")
        {
            Content = JsonContent.Create(ResumeBody)
        };
        request.Headers.Add(CsrfGuardMiddleware.CsrfHeaderName, csrfToken);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // Pins the client contract documented on /auth/antiforgery: the request token is bound to the
    // principal it was issued for, so a token fetched while anonymous is rejected after login.
    [Fact]
    public async Task CookieMutation_WithAntiforgeryTokenFetchedBeforeLogin_Returns403()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();

        var preLoginToken = await client.GetAntiforgeryTokenAsync();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes")
        {
            Content = JsonContent.Create(ResumeBody)
        };
        request.Headers.Add(CsrfGuardMiddleware.CsrfHeaderName, preLoginToken);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be("CSRF validation failed.");
    }
}

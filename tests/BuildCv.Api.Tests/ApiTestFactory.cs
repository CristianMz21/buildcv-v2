using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BuildCv.Api.Tests;

public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "test-signing-key-min-32-characters-long-0123456789",
                ["Jwt:Issuer"] = "buildcv-api",
                ["Jwt:Audience"] = "buildcv-bff",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30"
            });
        });
    }

    public HttpClient CreateCookieClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
}

internal static class TestHelpers
{
    public const string CandidateEmail = "candidate@example.com";
    public const string RecruiterEmail = "recruiter@example.com";
    public const string Password = "Str0ng!Password#2026";

    public static async Task<HttpResponseMessage> RegisterAsync(
        this HttpClient client, string email, string? role = null) =>
        await client.PostAsJsonAsync("/auth/register", new { email, password = Password, role });

    public static async Task<HttpResponseMessage> LoginAsync(this HttpClient client, string email) =>
        await client.PostAsJsonAsync("/auth/login", new { email, password = Password });

    public static async Task<string> LoginAndGetAccessTokenAsync(this HttpClient client, string email)
    {
        var response = await client.LoginAsync(email);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenBody>();
        return body!.AccessToken;
    }

    public static async Task<(string Email, string AccessToken)> RegisterAndLoginAsync(
        this HttpClient client, string email, string? role = null)
    {
        (await client.RegisterAsync(email, role)).EnsureSuccessStatusCode();
        var token = await client.LoginAndGetAccessTokenAsync(email);
        return (email, token);
    }

    public static HttpRequestMessage WithBearer(this HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static string GetSetCookie(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie")
            .First(v => v.StartsWith(name + "=", StringComparison.Ordinal));

    public static string GetCookieValue(HttpResponseMessage response, string name) =>
        GetSetCookie(response, name).Split(';')[0];

    private sealed record TokenBody(string AccessToken, int ExpiresIn);
}

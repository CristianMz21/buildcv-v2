using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BuildCv.Api.Tests;

// Defaults to Development because most tests rely on the relaxed local-http behavior; pass a
// different environment name to exercise the production-shaped configuration. `configureServices`
// runs after the app's own registrations, so a test can swap an adapter — the only way to reach
// failure branches that the in-memory repositories never take.
public sealed class ApiTestFactory(
    string? environment = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    // Exposed so tests can mint tokens the API will accept — an expired one, or one signed with
    // the wrong key — to prove what each authentication scheme does and does not accept.
    public const string SigningKey = "test-signing-key-min-32-characters-long-0123456789";
    public const string Issuer = "buildcv-api";
    public const string Audience = "buildcv-bff";

    private readonly string _environment = environment ?? Environments.Development;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);

        // UseSetting, not the in-memory collection below, and the difference is load-bearing. Choosing a
        // persistence provider happens while services are being REGISTERED, which is before
        // ConfigureAppConfiguration's sources are attached — a value added there arrives too late and the
        // host registers the SQL Server repositories, then fails every request on a connection it was
        // never meant to open. UseSetting writes into the host configuration WebApplication.CreateBuilder
        // reads, which is the same channel UseEnvironment above already travels on.
        //
        // These tests are about HTTP behavior, not storage, so they run on the in-memory store and never
        // need a database. The acknowledgement key is what lets the one test that deliberately builds a
        // Staging-shaped host keep using it: outside Development the in-memory provider refuses to
        // register without it.
        builder.UseSetting("Persistence:Provider", "InMemory");
        builder.UseSetting("Persistence:AllowInMemoryOutsideDevelopment", "true");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = SigningKey,
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30",
                // AddInfrastructure requires a usable Encryption key ring and validates it on start,
                // so every host the tests build needs one. Test-only material, distinct from the
                // committed development keys.
                ["Encryption:ActiveKeyId"] = "test-v1",
                ["Encryption:Keys:test-v1:Aes"] = "Z6h2YbISQC6Wo2Xbs2xQr1PistFWXwHrenrptzxtc6o=",
                ["Encryption:BlindIndex:ActiveKeyId"] = "test-b1",
                ["Encryption:BlindIndex:Keys:test-b1"] = "Xw273xuvdyoZuGb8kJo1vYXumxFtiHqIZkntZaZLegs="
            });
        });

        if (configureServices is not null)
            builder.ConfigureTestServices(configureServices);
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

    // The antiforgery request token is bound to the current principal, so callers must fetch it
    // after authenticating. See the /auth/antiforgery endpoint comment.
    public static async Task<string> GetAntiforgeryTokenAsync(this HttpClient client) =>
        (await client.GetAntiforgeryTokenAndCookieAsync()).RequestToken;

    // Both halves of the double-submit pair, for tests that drive cookies by hand. `authCookie`
    // decides which principal the request token gets bound to: pass the caller's access-token
    // cookie for an authenticated binding, omit it for an anonymous one.
    public static async Task<(string RequestToken, string Cookie)> GetAntiforgeryTokenAndCookieAsync(
        this HttpClient client, string? authCookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/antiforgery");
        if (authCookie is not null)
            request.Headers.Add("Cookie", authCookie);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AntiforgeryBody>();
        return (body!.RequestToken, GetCookieValue(response, "XSRF-TOKEN"));
    }

    public static string GetSetCookie(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie")
            .First(v => v.StartsWith(name + "=", StringComparison.Ordinal));

    public static string GetCookieValue(HttpResponseMessage response, string name) =>
        GetSetCookie(response, name).Split(';')[0];

    // Skips the name=value pair so a cookie whose VALUE happens to contain the attribute text
    // cannot produce a false positive.
    public static bool HasCookieAttribute(HttpResponseMessage response, string cookieName, string attribute) =>
        GetSetCookie(response, cookieName)
            .Split(';')
            .Skip(1)
            .Any(a => a.Trim().Equals(attribute, StringComparison.OrdinalIgnoreCase));

    private sealed record TokenBody(string AccessToken, int ExpiresIn);

    private sealed record AntiforgeryBody(string RequestToken);
}

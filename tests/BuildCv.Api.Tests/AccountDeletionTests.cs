using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// DELETE /v1/auth/me over HTTP: leaving, and taking everything with you.
//
// The product holds full employment histories, phone numbers and the list of vacancies somebody was
// quietly applying to. Until this route existed a candidate could delete one CV at a time and never the
// account behind it, so the address and the password hash stayed indefinitely. The tests that matter here
// are the ones that check what is GONE afterwards, and the one that checks nothing is gone when the
// request is refused.
public sealed class AccountDeletionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Password = TestHelpers.Password;

    [Fact]
    public async Task Delete_TakesTheResumesAndTheirDerivedScoresWithIt()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);
        // Asserted before the delete so the 404 afterwards is evidence the row went, rather than evidence
        // it was never written.
        (await GetStatusAsync(client, token, $"/v1/resumes/{resumeId}")).Should().Be(HttpStatusCode.OK);

        (await DeleteAccountAsync(client, token, Password)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The token still parses and still names the account — a JWT is stateless, so this is the honest
        // test of whether the DATA went, not of whether the credential was revoked.
        (await GetStatusAsync(client, token, $"/v1/resumes/{resumeId}"))
            .Should().Be(HttpStatusCode.NotFound, "the resume left with the account");
    }

    // The address has to come back, or "delete my account" leaves the person unable to return. It is the
    // filtered unique index on EmailHash that makes this work, and it reads the tombstone.
    [Fact]
    public async Task Delete_FreesTheEmailAddressForRegisteringAgain()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        (await DeleteAccountAsync(client, token, Password)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/register")
        {
            Content = JsonContent.Create(
                new { email = TestHelpers.CandidateEmail, password = Password }, options: Web)
        };
        var reregistered = await client.SendAsync(request);

        reregistered.StatusCode.Should().Be(
            HttpStatusCode.Created, "a deleted account must not hold its address hostage");
    }

    // An access token is a bearer credential. Without this check a stolen one would be enough to erase
    // somebody's entire employment history, with no way back and no second factor anywhere in the flow.
    [Fact]
    public async Task Delete_WithoutTheCurrentPassword_RefusesAndDestroysNothing()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var refused = await DeleteAccountAsync(client, token, "Wr0ng!Password#2026");

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetStatusAsync(client, token, $"/v1/resumes/{resumeId}")).Should().Be(
            HttpStatusCode.OK, "a refused deletion must leave the account exactly as it was");
    }

    // THE ONE THAT WOULD HURT MOST IF IT WERE WRONG. The organization check runs before anything is
    // destroyed, so a caller who is told to deal with their organization still has every CV when they come
    // back. Ordering it the other way round would delete the resumes and then refuse.
    [Fact]
    public async Task Delete_WhenBlockedByAnOrganization_LeavesEveryResumeIntact()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, ownerToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, ownerToken);

        var organizationId = await CreateOrganizationAsync(client, ownerToken);
        var (_, memberToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail);
        var memberId = await AccountIdOfAsync(client, memberToken);
        await AddMemberAsync(client, ownerToken, organizationId, memberId);

        var refused = await DeleteAccountAsync(client, ownerToken, Password);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain(
            "only owner", "the refusal has to name what the caller must do about it");

        (await GetStatusAsync(client, ownerToken, $"/v1/resumes/{resumeId}")).Should().Be(
            HttpStatusCode.OK, "nothing may be destroyed before the refusal is decided");

    }

    // A solo organization has nobody to strand, so it closes with the account rather than blocking it.
    [Fact]
    public async Task Delete_WithASoloOrganization_Succeeds()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var organizationId = await CreateOrganizationAsync(client, token);

        (await DeleteAccountAsync(client, token, Password)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await GetStatusAsync(client, token, $"/v1/organizations/{organizationId}")).Should().Be(
            HttpStatusCode.NotFound, "an organization nobody belongs to cannot be reached by any route");
    }

    private static async Task<Guid> AccountIdOfAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/me").WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static HttpClient BearerClient(ApiTestFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static async Task<HttpResponseMessage> DeleteAccountAsync(
        HttpClient client, string token, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/auth/me")
        {
            Content = JsonContent.Create(new { currentPassword = password }, options: Web)
        }.WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpStatusCode> GetStatusAsync(HttpClient client, string token, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route).WithBearer(token);
        var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<Guid> CreateResumeAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes")
        {
            Content = JsonContent.Create(
                new { fullName = "Jane Doe", email = "jane@example.com" }, options: Web)
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateOrganizationAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/organizations")
        {
            Content = JsonContent.Create(new { name = "Contoso", slug = "contoso" }, options: Web)
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task AddMemberAsync(
        HttpClient client, string token, Guid organizationId, Guid accountId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/organizations/{organizationId}/members")
        {
            Content = JsonContent.Create(new { accountId, role = "Member" }, options: Web)
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

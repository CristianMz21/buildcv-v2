using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// THE TWO ENUM PARSE SITES #21 DID NOT COVER, and they are not the same defect twice.
//
// Enum.TryParse accepts ANY numeric string, so a value outside the enum parses successfully and reaches
// the column. Both roles here are int-backed enums mapped to tinyint (AccountConfiguration,
// OrganizationConfiguration), and the tinyint conversion is unchecked — so "99" stores as 99, "300"
// truncates to 44 and "-1" wraps to 255. What differs is whether anything downstream stops it:
//
//   * POST /v1/organizations/{id}/members — NOTHING stops it. Organization.AddMember takes the role as
//     given, so the corruption is real and durable, and a membership whose role is neither Owner nor
//     Admin nor Member is a row every reader has to guess about. RemoveMember's "cannot remove the only
//     owner" rule reads Role == Owner, so 255 quietly satisfies "not an owner". Numeric input that
//     answered 200 answers 400 now; that is the behaviour change this file records.
//
//   * POST /v1/auth/register — the value is already refused, but by an allow-list two layers in.
//     RegisterAccountHandler.IsSelfAssignable is `role is Candidate or Recruiter`, and an undefined
//     value is neither, so today's answer is 400 "Role is not available for self-registration."
//     THE STATUS DOES NOT CHANGE, so these tests assert the DETAIL: that is the only observable that
//     tells the boundary check from the allow-list. The guard is worth having anyway, because
//     IsSelfAssignable exists to answer "may a stranger grant themselves this?" and not "is this a
//     member of the enum?" — the day it is written as `role != Role.Admin`, or the day a second
//     endpoint parses a Role, the undefined value reaches the tinyint with nothing in its way.
//
// WHAT ISDEFINED STILL DOES NOT CLOSE, kept reachable on purpose and pinned below. Enum.TryParse
// OR-combines comma-separated members even on a non-flags enum, and it accepts a leading '+'. Both
// yield a DEFINED member, which is what the tinyint column and every reader downstream assume, so
// neither is corruption. Rejecting them here and not on the four resume routes #21 covered would give
// one API two enum-parsing contracts for the same input shape, and narrowing to names only would also
// drop the numeric tolerance #21 deliberately kept. If that narrowing is ever wanted it should sweep
// all six sites at once.
public sealed class EnumGuardTests
{
    // ---- POST /v1/organizations/{id}/members -------------------------------------------------

    // The numeric cases are the ones that changed. Before the guard every one of them answered 200 and
    // stored a Membership.Role that is not a member of MembershipRole; "-1" reached the tinyint as 255.
    [Theory]
    [InlineData("99")]
    [InlineData("300")]
    [InlineData("-1")]
    [InlineData("Manager")]
    [InlineData("")]
    public async Task AddMember_WithARoleTheEnumDoesNotKnow_IsABadRequest(string role)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (owner, memberId) = await SetUpOrganizationAsync(client);

        var response = await PostAsync(
            client, owner.Token, $"/v1/organizations/{owner.OrganizationId}/members",
            new { accountId = memberId, role });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Be("Invalid membership role.");

        // Rejected BEFORE the handler ran, so the founder is still the only member and nothing was
        // written. A 400 produced after a successful write would look identical on the status alone.
        (await ReadMembersAsync(client, owner)).GetArrayLength().Should().Be(1);
    }

    // A DEFINED number still works, and the guard must not start rejecting one. GET answers the role as
    // a NAME, so a round-tripping client never needs this — what the tolerance protects is every client
    // written against the pre-v1 shape, where the read side answered the integer.
    [Theory]
    [InlineData("0", "Owner")]
    [InlineData("1", "Admin")]
    [InlineData("2", "Member")]
    public async Task AddMember_AcceptsAValidNumericRole_SoAnOldClientKeepsWorking(
        string role, string expected)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (owner, memberId) = await SetUpOrganizationAsync(client);

        var response = await PostAsync(
            client, owner.Token, $"/v1/organizations/{owner.OrganizationId}/members",
            new { accountId = memberId, role });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        RoleOf(await ReadMembersAsync(client, owner), memberId).Should().Be(expected);
    }

    // The two shapes IsDefined does not close, stated as behaviour rather than left to be rediscovered.
    // "Owner,Member" is 0|2 = 2 = Member and "+1" is Admin: both are real members, so both are stored
    // and both come back as a name the client never sent.
    [Theory]
    [InlineData("Owner,Member", "Member")]
    [InlineData("+1", "Admin")]
    public async Task AddMember_AcceptsCombinedAndSignedInput_WhichIsDefinedButNotWhatWasNamed(
        string role, string expected)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (owner, memberId) = await SetUpOrganizationAsync(client);

        var response = await PostAsync(
            client, owner.Token, $"/v1/organizations/{owner.OrganizationId}/members",
            new { accountId = memberId, role });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        RoleOf(await ReadMembersAsync(client, owner), memberId).Should().Be(expected);
    }

    // ---- POST /v1/auth/register --------------------------------------------------------------

    // The DETAIL is the assertion, not the status: an undefined role was already a 400 here, refused by
    // RegisterAccountHandler's self-assignment allow-list rather than by the parse. Asserting the status
    // alone would be green with the guard removed, which is the whole failure mode this repository keeps
    // catching — two causes, one observable.
    [Theory]
    [InlineData("99")]
    [InlineData("300")]
    [InlineData("-1")]
    [InlineData("Manager")]
    [InlineData("")]
    public async Task Register_WithARoleTheEnumDoesNotKnow_IsRefusedAtTheBoundary(string role)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsJsonAsync("/v1/auth/register", new
        {
            email = TestHelpers.CandidateEmail,
            password = TestHelpers.Password,
            role
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Be("Invalid role.",
            "the parse must refuse a value that is not a member of Role — reaching "
            + "\"Role is not available for self-registration.\" means the undefined value got past the "
            + "boundary and was stopped by an allow-list that exists for a different question");
    }

    [Theory]
    [InlineData("0", "Candidate")]
    [InlineData("1", "Recruiter")]
    public async Task Register_AcceptsAValidNumericRole_SoAnOldClientKeepsWorking(
        string role, string expected)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsJsonAsync("/v1/auth/register", new
        {
            email = TestHelpers.CandidateEmail,
            password = TestHelpers.Password,
            role
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("role").GetString().Should().Be(expected);
    }

    // The register side of the two shapes IsDefined does not close. "Candidate,Recruiter" is 0|1 = 1 =
    // Recruiter: a role the caller never named, granted. It is not an escalation — registration lets a
    // stranger ask for Recruiter outright — and Admin stays unreachable because it is not
    // self-assignable, which the second case pins so that "combining is harmless" is measured rather
    // than assumed.
    [Theory]
    [InlineData("Candidate,Recruiter", HttpStatusCode.Created, "Recruiter")]
    [InlineData("+1", HttpStatusCode.Created, "Recruiter")]
    [InlineData("Candidate,Admin", HttpStatusCode.BadRequest, null)]
    public async Task Register_CombinedAndSignedInput_IsDefinedButNotWhatWasNamed(
        string role, HttpStatusCode expectedStatus, string? expectedRole)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsJsonAsync("/v1/auth/register", new
        {
            email = TestHelpers.CandidateEmail,
            password = TestHelpers.Password,
            role
        });

        response.StatusCode.Should().Be(expectedStatus);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (expectedRole is null)
        {
            body.RootElement.GetProperty("detail").GetString()
                .Should().Be("Role is not available for self-registration.",
                    "Candidate|Admin is a DEFINED member, so the parse accepts it and the "
                    + "self-assignment allow-list is what refuses it");
            return;
        }

        body.RootElement.GetProperty("role").GetString().Should().Be(expectedRole);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private sealed record Founder(string Token, Guid OrganizationId);

    private static async Task<(Founder Owner, Guid MemberId)> SetUpOrganizationAsync(HttpClient client)
    {
        var (_, ownerToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var registered = await client.RegisterAsync(TestHelpers.RecruiterEmail, role: "Recruiter");
        registered.EnsureSuccessStatusCode();
        using var member = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        var memberId = member.RootElement.GetProperty("id").GetGuid();

        var created = await PostAsync(client, ownerToken, "/v1/organizations",
            new { name = "Contoso", slug = "contoso" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var organization = JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        return (new Founder(ownerToken, organization.RootElement.GetProperty("id").GetGuid()), memberId);
    }

    private static async Task<JsonElement> ReadMembersAsync(HttpClient client, Founder owner)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/organizations/{owner.OrganizationId}").WithBearer(owner.Token);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // Cloned, because the JsonDocument is disposed when this method returns.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("members").Clone();
    }

    private static string RoleOf(JsonElement members, Guid accountId) =>
        members.EnumerateArray()
            .Single(member => member.GetProperty("accountId").GetGuid() == accountId)
            .GetProperty("role").GetString()!;

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string token, string route, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload)
        }.WithBearer(token);

        return await client.SendAsync(request);
    }
}

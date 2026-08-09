using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Api.Contracts;
using BuildCv.Application.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildCv.Api.Tests;

// THE ACCOUNT BODY, PINNED BEFORE IT WAS REFACTORED.
//
// POST /v1/auth/register, POST /v1/auth/change-password and GET /v1/auth/me all answered
// Application.Identity.AccountDto directly — an Application type on the wire, which this repository's
// own rules forbid and which every other route stopped doing at v1. It passed V1ContractShapeTests
// only by luck: that sweep fails an enum rendered as a NUMBER, and AccountDto.From happens to call
// ToString() on Role and Status.
//
// Swapping it for Contracts.AccountResponse changes nothing a client can see, and that is the whole
// point of doing it now rather than after the frontend binds: the same refactor becomes a /v2 the day
// it is not free. These assertions were written and run GREEN against AccountDto, so they are a record
// of what the API answered before the change rather than a description of what it answers after.
//
// THE ORDER IS ASSERTED, not just the set. System.Text.Json writes properties in declaration order, so
// a reordered record is a reordered body — invisible to a `Should().Contain` and visible to anything
// that diffs responses or regenerates a typed client.
public sealed class AuthContractTests
{
    // The full property list, in order, for the one shape three routes answer.
    private static readonly string[] AccountFields =
        ["id", "email", "role", "status", "isEmailVerified", "createdAt", "lastLoginAt"];

    [Fact]
    public async Task TheThreeAuthRoutesThatAnswerAnAccount_AllAnswerTheSameShape()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var register = await client.RegisterAsync(TestHelpers.CandidateEmail);
        register.StatusCode.Should().Be(HttpStatusCode.Created);
        using var registered = JsonDocument.Parse(await register.Content.ReadAsStringAsync());

        var token = await client.LoginAndGetAccessTokenAsync(TestHelpers.CandidateEmail);

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/me").WithBearer(token);
        var me = await client.SendAsync(meRequest);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        using var read = JsonDocument.Parse(await me.Content.ReadAsStringAsync());

        using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/change-password")
        {
            Content = JsonContent.Create(new
            {
                currentPassword = TestHelpers.Password,
                newPassword = "An0ther!Password#2026"
            })
        }.WithBearer(token);
        var changed = await client.SendAsync(changeRequest);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rotated = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());

        foreach (var (route, body) in new[]
        {
            ("POST /v1/auth/register", registered.RootElement),
            ("GET /v1/auth/me", read.RootElement),
            ("POST /v1/auth/change-password", rotated.RootElement)
        })
        {
            body.EnumerateObject().Select(property => property.Name).Should().Equal(AccountFields,
                "{0} answers the account shape, in declaration order", route);
        }

        // THE TYPES, not only the names. A field can keep its name and change from a string to a number
        // — which is exactly the regression V1ContractShapeTests exists for on the enum fields — and a
        // name-only assertion would not notice.
        var account = read.RootElement;
        account.GetProperty("id").GetGuid().Should().NotBeEmpty();
        account.GetProperty("email").GetString().Should().Be(TestHelpers.CandidateEmail);
        account.GetProperty("role").GetString().Should().Be("Candidate",
            "the role is the enum NAME on the wire, never the tinyint behind it");
        account.GetProperty("status").GetString().Should().Be("Active");
        account.GetProperty("isEmailVerified").ValueKind.Should().Be(JsonValueKind.False);
        account.GetProperty("createdAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.MinValue);
        account.GetProperty("lastLoginAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.MinValue,
            "the caller has logged in by the time /v1/auth/me is reachable");

        // The one nullable member, asserted where it really is null. Registering does not log you in, so
        // the 201 is the only body that can prove `lastLoginAt` is emitted-as-null rather than omitted —
        // a client typing it as optional needs the property to exist.
        registered.RootElement.GetProperty("lastLoginAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // THE REFACTOR PROVED HARMLESS, CHARACTER FOR CHARACTER.
    //
    // The test above records what the routes answer; this one compares the two TYPES directly, which is
    // the assertion that keeps working when nobody remembers what the body used to look like. A renamed
    // property, a reordered one, a widened type, an extra member on either side — each of them changes
    // one of these strings and not the other.
    //
    // THE SERIALIZER IS THE HOST'S OWN, resolved out of the composed application rather than constructed
    // here from JsonSerializerDefaults.Web. A hand-built options object would be a second statement of
    // how this API serializes, and the day someone calls ConfigureHttpJsonOptions the two would disagree
    // while this test kept reporting that the shapes match.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AccountResponse_SerializesIdenticallyToTheAccountDtoItReplaced(bool hasLoggedIn)
    {
        using var factory = new ApiTestFactory();
        var options = factory.Services
            .GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        // Both nullable states, because `lastLoginAt` is the only member whose two cases can serialize
        // differently — an option that omits nulls would drop the property from one shape and not the
        // other, and the populated case alone would never show it.
        var dto = new AccountDto(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "candidate@example.com",
            "Candidate",
            "Active",
            IsEmailVerified: true,
            CreatedAt: new DateTimeOffset(2026, 8, 9, 10, 30, 0, TimeSpan.Zero),
            LastLoginAt: hasLoggedIn ? new DateTimeOffset(2026, 8, 9, 11, 0, 0, TimeSpan.Zero) : null);

        var before = JsonSerializer.Serialize(dto, options);
        var after = JsonSerializer.Serialize(AccountResponse.From(dto), options);

        after.Should().Be(before,
            "moving the account off an Application type must be invisible to every client — the day it "
            + "is not, this is a /v2");

        // WITHOUT THIS THE COMPARISON COULD BE VACUOUS. Two serializations that both produced "{}" — a
        // type with no public members, an options object that ignored everything — would satisfy the
        // line above while proving nothing.
        before.Should().Contain("\"lastLoginAt\"").And.Contain("\"isEmailVerified\"");
    }
}

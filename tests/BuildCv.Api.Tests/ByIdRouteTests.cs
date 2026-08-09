using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// THE EMPTY GUID, WHICH EVERY BY-ID ROUTE USED TO ANSWER WITH A 500 AND A C# PARAMETER NAME.
//
// The `:guid` route constraint matches 00000000-0000-0000-0000-000000000000 — it is a well-formed guid
// — and the endpoint lambda then built a strongly-typed id from it OUTSIDE any try. The id threw
// ArgumentException("...must not be empty.", nameof(value)), which matched no IExceptionHandler branch,
// so GlobalExceptionHandler answered:
//
//     {"title":"Internal Server Error","status":500,
//      "detail":"AnalysisId must not be empty. (Parameter 'value')"}
//
// Two defects. A refusal the caller caused was reported as a server fault, and `(Parameter 'value')`
// named an internal parameter in a response body — the same thing ResumeDraftValidator's comment
// refuses to put on a review screen. The fix is one Domain type, EmptyIdentifierException, so every
// route and every request body that builds an id is covered by one change rather than twenty.
//
// SWEPT ACROSS ROUTES, not asserted on one. The whole claim is that this is fixed at a shared boundary,
// and a single-route test would be satisfied by a single-route patch.
public sealed class ByIdRouteTests
{
    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    // Every v1 route that takes a guid in its path and builds a strongly-typed id from it. Five of the
    // six id types are reached here; AccountId is reached through the request body below, because no
    // route takes an account id in its path except the organization membership delete.
    public static TheoryData<string> ByIdRoutes => new()
    {
        $"/v1/scoring/{EmptyGuid}",
        $"/v1/readability/{EmptyGuid}",
        $"/v1/resumes/{EmptyGuid}",
        $"/v1/jobs/{EmptyGuid}",
        $"/v1/organizations/{EmptyGuid}"
    };

    [Theory]
    [MemberData(nameof(ByIdRoutes))]
    public async Task AByIdRoute_GivenTheEmptyGuid_Is400AndNamesNoParameter(string route)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Get, route).WithBearer(token);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an id that can name no row is the caller's input being wrong, not this server failing");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().Be("Bad Request");
        body.RootElement.GetProperty("status").GetInt32().Should().Be(400);

        var detail = body.RootElement.GetProperty("detail").GetString();

        // THE LEAK, ASSERTED DIRECTLY. The status alone would be satisfied by any 400 — including one
        // that still carried "(Parameter 'value')" in its detail — so the two are checked apart.
        detail.Should().NotBeNull().And.NotContain("Parameter",
            "a C# parameter name must never reach a response body");
        detail.Should().EndWith("must not be empty.",
            "the refusal still has to say what was wrong with the request");
    }

    // THE SAME DEFECT ARRIVING IN A REQUEST BODY, which is the case that decided the fix. A route
    // constraint on `:guid` would have covered the five routes above in one line and left this one
    // answering 500 — and 404 is not a coherent answer to a body carrying two ids, only one of which is
    // empty. Fixing it in the id type covers both with one rule and one status.
    [Fact]
    public async Task AnEmptyGuidInARequestBody_Is400AndNamesNoParameter()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/scoring/score")
        {
            Content = JsonContent.Create(new { resumeId = Guid.Empty, jobPostingId = Guid.NewGuid() })
        }.WithBearer(token);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString()
            .Should().Be("ResumeId must not be empty.",
                "the field that was wrong is named, and nothing else is");
    }

    // THE CONTROL FOR THE CONTROL. A 400 on the empty guid would also be produced by a route that had
    // simply stopped working, so a NON-empty unknown guid is asserted to still reach the handler and
    // answer 404 — the two inputs differ only in the guid, and they must land on different codes.
    [Theory]
    [MemberData(nameof(ByIdRoutes))]
    public async Task AByIdRoute_GivenAnUnknownButUsableGuid_IsStill404(string route)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var unknown = route.Replace(EmptyGuid, Guid.NewGuid().ToString(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, unknown).WithBearer(token);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the empty guid is refused for being unusable, not because by-id reads stopped working");
    }
}

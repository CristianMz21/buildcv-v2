using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// THE ASSERTION THAT MAKES THE v1 PROMISE CHECKABLE RATHER THAN CLAIMED. Every other contract test in
// this suite pins one endpoint's shape; each DTO's comment block then states the convention, and a
// per-endpoint test cannot tell a convention that holds everywhere from one that holds where somebody
// happened to write a test. That gap is exactly how the resume routes shipped an aggregate — enveloped
// ids and four raw enum integers — while two DTO files described v1 as settled.
//
// So this walks REAL RESPONSE BODIES from every v1 route that returns one, structurally:
//
//   1. No JSON object may consist of exactly one property named `value` or `name`. That is the shape a
//      strongly-typed Domain record serializes into — {"value": guid} for every id, {"value": string}
//      for PersonName/Email/PhoneNumber/Url/OrganizationName/Slug, {"name": string} for Technology —
//      and it is precise: a single-property object keyed anything else passes, and a multi-property
//      object carrying a `name` field (a skill, a language, an interest) passes.
//   2. No property whose name denotes an ENUM may be a number. The names are listed rather than
//      inferred, because nothing in JSON distinguishes an enum from a score.
//
// Both rules run over the WHOLE tree of each body, so a wrapper or an integer enum nested three levels
// inside a paged list of analyses is caught by the same walk that catches a top-level one.
public sealed class V1ContractShapeTests
{
    // Every field on any v1 response that carries an enum. `status` is here for both JobPostingStatus
    // and OrganizationStatus; `type`, `level`, `role`, `priority`, `kind`, `section`, `band`,
    // `minimumLevel`, `educationLevel`, `overall` and `confidence` cover the rest. Deliberately NOT
    // here: `overallScore`, `weight`, `impact`, `yearsOfExperience`, `pageCount` and `schemaVersion`,
    // which are genuinely numeric.
    private static readonly HashSet<string> EnumFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "band", "status", "priority", "kind", "section", "level", "type", "role", "minimumLevel",
        "educationLevel", "overall", "confidence"
    };

    [Fact]
    public async Task EveryV1ResponseBody_CarriesNoWrapperObjectAndNoEnumAsANumber()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // Four auth requests, which is the whole 5/min window a TestServer client gets: every request
        // lands in the same "unknown" partition because Connection.RemoteIpAddress is never populated.
        // A third account would 429 rather than fail the assertion it was added for.
        var (_, candidate) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiter) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var bodies = new List<(string Route, JsonElement Body)>();

        async Task<JsonElement> Collect(string route, HttpMethod method, string token, object? body = null)
        {
            using var request = new HttpRequestMessage(method, route).WithBearer(token);
            if (body is not null)
                request.Content = JsonContent.Create(body);

            var response = await client.SendAsync(request);
            response.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Created],
                "the sweep only means anything over bodies the API really produced — {0} failed", route);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var element = json.RootElement.Clone();
            bodies.Add((route, element));
            return element;
        }

        await Collect("/v1/auth/me", HttpMethod.Get, candidate);
        var recruiterAccountId = (await Collect("/v1/auth/me", HttpMethod.Get, recruiter))
            .GetProperty("id").GetGuid();

        // The richest resume the API can build: every one of the ten owned collections populated, so a
        // wrapper surviving in any of them is reachable by this walk.
        var resumeId = (await Collect(
            "/v1/resumes/import", HttpMethod.Post, candidate, ResumeImportTests.FullDraft()))
            .GetProperty("id").GetGuid();
        await Collect($"/v1/resumes/{resumeId}", HttpMethod.Get, candidate);
        await Collect("/v1/resumes?limit=5", HttpMethod.Get, candidate);

        var jobId = (await Collect("/v1/jobs", HttpMethod.Post, recruiter, new
        {
            title = "Senior Backend Engineer",
            companyName = "Contoso",
            companyId = (Guid?)null,
            description = "Build deterministic scoring systems."
        })).GetProperty("id").GetGuid();
        await Collect($"/v1/jobs/{jobId}", HttpMethod.Get, recruiter);
        await Collect($"/v1/jobs/{jobId}/publish", HttpMethod.Post, recruiter);

        await Collect("/v1/job-offers/import", HttpMethod.Post, candidate, new
        {
            title = "Staff Engineer",
            companyName = "Globex",
            requirements = new[] { new { skill = "C#", priority = "MustHave" } }
        });
        await Collect("/v1/job-offers/extract", HttpMethod.Post, candidate,
            new { text = "Our stack is C# and Docker." });

        var analysisId = (await Collect("/v1/scoring/score", HttpMethod.Post, candidate,
            new { resumeId, jobPostingId = jobId })).GetProperty("id").GetGuid();
        await Collect($"/v1/scoring/{analysisId}", HttpMethod.Get, candidate);
        await Collect($"/v1/resumes/{resumeId}/analyses", HttpMethod.Get, candidate);

        var organizationId = (await Collect("/v1/organizations", HttpMethod.Post, candidate,
            new { name = "Contoso", slug = "contoso" })).GetProperty("id").GetGuid();
        await Collect($"/v1/organizations/{organizationId}", HttpMethod.Get, candidate);
        await Collect("/v1/organizations/slug/contoso", HttpMethod.Get, candidate);

        // A second member, so `members[]` carries a role that was WRITTEN as a name ("Admin") and has
        // to read back as one. The founder's Owner row alone would pass on the enum's default.
        await Collect($"/v1/organizations/{organizationId}/members", HttpMethod.Post, candidate,
            new { accountId = recruiterAccountId, role = "Admin" });

        // The two multipart routes, which no JSON body can reach. /propose is the one that matters to
        // rule 2: its response carries `confidence.overall` and a `confidence` per field, all enum
        // names, and it is the only place those two property names appear on the wire.
        await CollectUploadAsync(client, bodies, "/v1/resumes/import/extract", candidate);
        await CollectUploadAsync(client, bodies, "/v1/resumes/import/propose", candidate);

        // Guards the sweep itself: an empty or truncated list would pass both rules vacuously, and this
        // is the number of routes the walk is claimed to cover.
        bodies.Should().HaveCount(19);

        foreach (var (route, body) in bodies)
        {
            AssertNoWrappersOrNumericEnums(body, route, "$");
        }
    }

    private static async Task CollectUploadAsync(
        HttpClient client, List<(string Route, JsonElement Body)> bodies, string route, string token)
    {
        var filePart = new ByteArrayContent(
            System.Text.Encoding.UTF8.GetBytes("Jane Candidate\njane@example.com\n\nExperience\nAcme"));
        filePart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new MultipartFormDataContent { { filePart, "file", "cv.txt" } }
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the sweep only means anything over bodies the API really produced — {0} failed", route);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        bodies.Add((route, json.RootElement.Clone()));
    }

    private static void AssertNoWrappersOrNumericEnums(JsonElement element, string route, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();

                if (properties.Count == 1
                    && (properties[0].NameEquals("value") || properties[0].NameEquals("name")))
                {
                    throw new Xunit.Sdk.XunitException(
                        $"{route} answers a single-value wrapper at {path}: "
                        + $"{{\"{properties[0].Name}\": …}}. A strongly-typed Domain record is reaching "
                        + "the wire; map it through a DTO in Api/Contracts.");
                }

                foreach (var property in properties)
                {
                    if (EnumFieldNames.Contains(property.Name)
                        && property.Value.ValueKind == JsonValueKind.Number)
                    {
                        throw new Xunit.Sdk.XunitException(
                            $"{route} answers the enum `{property.Name}` as the number "
                            + $"{property.Value} at {path}.{property.Name}. Those numbers are an "
                            + "append-only persistence detail; the wire carries ToString() names.");
                    }

                    AssertNoWrappersOrNumericEnums(property.Value, route, $"{path}.{property.Name}");
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    AssertNoWrappersOrNumericEnums(item, route, $"{path}[{index++}]");

                break;
        }
    }
}

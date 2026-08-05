using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// POST /job-offers/import and /extract over HTTP, and the whole product loop they complete:
// import an offer, import a resume, score, get advice. The headline test asserts REAL recommendations,
// not merely a 200 -- that is the thing this phase delivered.
public sealed class JobOfferImportTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- the loop the product was named for -------------------------------------------------------

    [Fact]
    public async Task TheWholeLoop_ImportOffer_ImportResume_Score_ReturnsRealRecommendations()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        // An offer that lists C# as a must-have, against a resume that does not have it.
        var jobId = await ImportOfferAsync(client, token, MustHave("C#"));
        var resumeId = await CreateResumeAsync(client, token);

        var score = await ScoreAsync(client, token, resumeId, jobId);
        score.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await score.Content.ReadAsStringAsync());
        var recommendations = json.RootElement.GetProperty("recommendations").EnumerateArray().ToList();
        recommendations.Should().NotBeEmpty("a scored resume that misses a must-have skill must get advice");

        var missingSkill = recommendations.Should().ContainSingle(r =>
            r.GetProperty("kind").GetString() == "MissingMustHaveSkill").Subject;
        missingSkill.GetProperty("message").GetString().Should().Contain("'C#'",
            "the advice must name the skill the candidate is missing");
        missingSkill.GetProperty("priority").GetString().Should().Be("Critical",
            "an unmet must-have of full weight lands above the Critical impact threshold");
    }

    // ---- the security hole this PR closes ---------------------------------------------------------

    // A candidate owns their imported Draft offer, and the publish route has no role policy -- so the
    // handler's CanPostJobs gate is the only thing stopping them from making it world-scorable. This goes
    // RED if that gate is removed.
    [Fact]
    public async Task ACandidate_CannotPublishTheirImportedOffer()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var jobId = await ImportOfferAsync(client, token, MustHave("C#"));

        using var publish = new HttpRequestMessage(HttpMethod.Post, $"/v1/jobs/{jobId}/publish").WithBearer(token);

        (await client.SendAsync(publish)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // A candidate's Draft offer scores for its OWNER and is Forbidden for anyone else -- the visibility
    // guarantee that makes a private offer private.
    [Fact]
    public async Task AnImportedDraftOffer_ScoresForItsOwner_AndIsForbiddenForAnyoneElse()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, ownerToken) = await client.RegisterAndLoginAsync("owner@example.com");
        var (_, strangerToken) = await client.RegisterAndLoginAsync("stranger@example.com");

        var jobId = await ImportOfferAsync(client, ownerToken, MustHave("C#"));

        var ownerScore = await ScoreAsync(client, ownerToken, await CreateResumeAsync(client, ownerToken), jobId);
        ownerScore.StatusCode.Should().Be(HttpStatusCode.OK, "the owner may score against their own draft");

        var strangerScore = await ScoreAsync(client, strangerToken, await CreateResumeAsync(client, strangerToken), jobId);
        strangerScore.StatusCode.Should().Be(HttpStatusCode.Forbidden, "nobody else may reach a private draft");
    }

    // ---- validation and the wire contract ---------------------------------------------------------

    [Fact]
    public async Task Import_WithAnUnknownPriority_IsAProblemDetailsKeyedByFieldPath()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostImportAsync(client, token, new
        {
            title = "Role",
            companyName = "Contoso",
            requirements = new[] { new { skill = "C#", priority = "Critical" } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errors").GetProperty("requirements[0].priority")[0].GetString()
            .Should().Be("Invalid requirement priority.");
    }

    // Distinct values in every field, because ImportJobOfferRequest mirrors JobOfferDraft by hand and a
    // swapped mapping would be a real bug. Read back through GET /jobs/{id}.
    [Fact]
    public async Task Import_RoundTripsEveryField_AndDerivesWeightFromPriority()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var jobId = await ImportOfferAsync(client, token, new
        {
            title = "Distinct Title",
            companyName = "Distinct Company",
            requirements = new[]
            {
                new { skill = "Rust", priority = "MustHave" },
                new { skill = "Kafka", priority = "NiceToHave" }
            }
        });

        var posting = await GetJobAsync(client, token, jobId);
        posting.GetProperty("title").GetString().Should().Be("Distinct Title");
        posting.GetProperty("companyName").GetProperty("value").GetString().Should().Be("Distinct Company");
        posting.GetProperty("status").GetInt32().Should().Be(0, "an imported offer is a Draft");

        var requirements = posting.GetProperty("requirements").EnumerateArray().ToList();
        requirements[0].GetProperty("skill").GetProperty("name").GetString().Should().Be("Rust");
        requirements[0].GetProperty("priority").GetInt32().Should().Be(0, "RequirementPriority.MustHave");
        requirements[0].GetProperty("weight").GetDouble().Should().Be(1.0, "weight derives from MustHave");
        requirements[1].GetProperty("skill").GetProperty("name").GetString().Should().Be("Kafka");
        requirements[1].GetProperty("priority").GetInt32().Should().Be(1, "RequirementPriority.NiceToHave");
        requirements[1].GetProperty("weight").GetDouble().Should().Be(0.5, "weight derives from NiceToHave");
    }

    [Fact]
    public async Task Extract_ProposesGuessedNiceToHaveRequirementsFromText()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/job-offers/extract")
        {
            Content = JsonContent.Create(new { text = "Our stack is C# and Docker." })
        }.WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var requirements = json.RootElement.GetProperty("requirements").EnumerateArray().ToList();
        requirements.Select(r => r.GetProperty("skill").GetString()).Should().Equal("C#", "Docker");
        requirements.Should().OnlyContain(r =>
            r.GetProperty("priority").GetString() == "NiceToHave" && r.GetProperty("priorityGuessed").GetBoolean());
    }

    // The CSRF guard covers this route because it covers every cookie-authenticated unsafe method, but a
    // route added to CsrfGuardMiddleware.ExemptPaths by mistake would fail no other test. Both directions,
    // so the 201 proves the 403 is about the token and not the request.
    [Fact]
    public async Task Import_FromACookieClientWithoutTheCsrfToken_IsForbidden()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/job-offers/import")
        {
            Content = JsonContent.Create(new { title = "Role", companyName = "Contoso", requirements = MustHave("C#") })
        };

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Import_FromACookieClientWithTheCsrfToken_IsCreated()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var csrfToken = await client.GetAntiforgeryTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/job-offers/import")
        {
            Content = JsonContent.Create(new { title = "Role", companyName = "Contoso", requirements = MustHave("C#") })
        };
        request.Headers.Add(CsrfGuardMiddleware.CsrfHeaderName, csrfToken);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static HttpClient BearerClient(ApiTestFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static object[] MustHave(string skill) => [new { skill, priority = "MustHave" }];

    private static async Task<HttpResponseMessage> PostImportAsync(HttpClient client, string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/job-offers/import")
        {
            Content = JsonContent.Create(body, options: Web)
        }.WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<Guid> ImportOfferAsync(HttpClient client, string token, object requirements)
    {
        var body = requirements is object[] reqs
            ? (object)new { title = "Senior Backend Engineer", companyName = "Contoso", requirements = reqs }
            : requirements;

        var response = await PostImportAsync(client, token, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetProperty("value").GetGuid();
    }

    private static async Task<Guid> CreateResumeAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes")
        {
            Content = JsonContent.Create(new
            {
                fullName = "Jane Candidate",
                email = "jane@example.com",
                phoneNumber = (string?)null,
                location = (string?)null,
                summary = (string?)null
            })
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetProperty("value").GetGuid();
    }

    private static async Task<HttpResponseMessage> ScoreAsync(HttpClient client, string token, Guid resumeId, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/scoring/score")
        {
            Content = JsonContent.Create(new { resumeId, jobPostingId = jobId })
        }.WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> GetJobAsync(HttpClient client, string token, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/jobs/{jobId}").WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}

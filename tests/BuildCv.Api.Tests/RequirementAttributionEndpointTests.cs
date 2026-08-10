using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// requirementMatches over HTTP: present on the call that computed it, absent on every stored read.
//
// The absence is the half worth testing, and it is the half a shape assertion would miss. Attribution
// computed when a STORED analysis is read would describe the resume as it is now beside a score taken from
// what it was -- the situation isStale exists to report -- and a client could not tell the difference. The
// separation is enforced in the type system (only ScoreResumeHandler returns ScoredAnalysisView), so these
// tests are what proves the type system was pointed at the right thing.
public sealed class RequirementAttributionEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Score_ReportsWhichEntryAnsweredEachRequirement_AndInTheCandidatesOwnWording()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        // The posting asks for "C#". The candidate wrote "C#" too -- the alias case is covered against the
        // real lexicon in the Application suite, where the aliases are stated by the test rather than read
        // out of the shipped file.
        var jobId = await ImportOfferAsync(client, token, "C#");
        var resumeId = await CreateResumeAsync(client, token);
        await AddSkillAsync(client, token, resumeId, "C#");

        var body = await ScoreAsync(client, token, resumeId, jobId);

        var matches = body.GetProperty("requirementMatches");
        matches.GetArrayLength().Should().Be(1, "one entry per requirement, always");

        var match = matches[0];
        match.GetProperty("skill").GetString().Should().Be("C#");
        match.GetProperty("priority").GetString().Should().Be("MustHave", "enums travel as names, never numbers");
        match.GetProperty("satisfied").GetBoolean().Should().BeTrue();

        var matchedBy = match.GetProperty("matchedBy");
        matchedBy.GetArrayLength().Should().Be(1);
        matchedBy[0].GetProperty("source").GetString().Should().Be("SkillName");
        matchedBy[0].GetProperty("matchedText").GetString().Should().Be("C#");
    }

    // The assertion that replaces the client's string-matching workaround. A requirement nothing answers
    // comes back explicitly unsatisfied rather than missing, so absence of a recommendation stops being
    // evidence of anything -- advice is capped at ten.
    [Fact]
    public async Task Score_AnUnansweredRequirement_IsReportedUnsatisfiedWithNoEvidence()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var jobId = await ImportOfferAsync(client, token, "C#");
        var resumeId = await CreateResumeAsync(client, token);

        var body = await ScoreAsync(client, token, resumeId, jobId);

        var match = body.GetProperty("requirementMatches")[0];
        match.GetProperty("satisfied").GetBoolean().Should().BeFalse();
        match.GetProperty("matchedBy").GetArrayLength().Should().Be(0);
    }

    // THE ONE THAT MATTERS. A stored analysis is served by two routes and neither may answer with
    // attribution, because neither can prove the resume still looks the way it did when the score was
    // taken. null, not an empty array: "not carried by this response" and "the posting required nothing"
    // are different facts and a client acts differently on them.
    [Fact]
    public async Task StoredReads_CarryNoAttributionAtAll_WhileTheScoringCallDoes()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var jobId = await ImportOfferAsync(client, token, "C#");
        var resumeId = await CreateResumeAsync(client, token);
        await AddSkillAsync(client, token, resumeId, "C#");

        var scored = await ScoreAsync(client, token, resumeId, jobId);
        // Asserted so the two absences below are evidence rather than a route that returns nothing at all.
        scored.GetProperty("requirementMatches").GetArrayLength().Should().Be(1);
        var analysisId = scored.GetProperty("id").GetGuid();

        var byId = await GetJsonAsync(client, token, $"/v1/scoring/{analysisId}");
        HasAttribution(byId).Should().BeFalse(
            "a stored analysis cannot prove the resume it scored still looks that way");

        var history = await GetJsonAsync(client, token, $"/v1/resumes/{resumeId}/analyses");
        var entries = history.GetProperty("items");
        entries.GetArrayLength().Should().BeGreaterThan(0, "an empty history would satisfy the next line vacuously");
        foreach (var entry in entries.EnumerateArray())
            HasAttribution(entry).Should().BeFalse("the history serves stored analyses too");
    }

    // De-duplication returns a stored row, and it STILL carries attribution -- which is sound for the one
    // reason the reuse itself is: the key's first term is ResumeUpdatedAt equality, so a reuse is proof the
    // resume has not moved. Omitting it here would leave a client unable to ever obtain attribution for a
    // pair it had already scored, because re-POSTing de-duplicates again.
    [Fact]
    public async Task Score_WhenItDeduplicates_StillReportsAttribution()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var jobId = await ImportOfferAsync(client, token, "C#");
        var resumeId = await CreateResumeAsync(client, token);
        await AddSkillAsync(client, token, resumeId, "C#");

        var first = await ScoreAsync(client, token, resumeId, jobId);
        var second = await ScoreAsync(client, token, resumeId, jobId);

        second.GetProperty("id").GetGuid().Should().Be(
            first.GetProperty("id").GetGuid(), "the second call must have de-duplicated for this to test anything");
        second.GetProperty("requirementMatches").GetArrayLength().Should().Be(1);
        second.GetProperty("requirementMatches")[0].GetProperty("satisfied").GetBoolean().Should().BeTrue();
    }

    private static bool HasAttribution(JsonElement analysis) =>
        analysis.TryGetProperty("requirementMatches", out var matches)
        && matches.ValueKind is not JsonValueKind.Null;

    private static HttpClient BearerClient(ApiTestFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static async Task<JsonElement> ScoreAsync(HttpClient client, string token, Guid resumeId, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/scoring/score")
        {
            Content = JsonContent.Create(new { resumeId, jobPostingId = jobId })
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string token, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route).WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<Guid> ImportOfferAsync(HttpClient client, string token, string requiredSkill)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/job-offers/import")
        {
            Content = JsonContent.Create(
                new
                {
                    title = "Senior Backend Engineer",
                    companyName = "Contoso",
                    requirements = new[] { new { skill = requiredSkill, priority = "MustHave" } }
                },
                options: Web)
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
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

    private static async Task AddSkillAsync(HttpClient client, string token, Guid resumeId, string skill)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/resumes/{resumeId}/skills")
        {
            Content = JsonContent.Create(new { skillName = skill }, options: Web)
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

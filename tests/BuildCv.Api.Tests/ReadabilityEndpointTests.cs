using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

public sealed class ReadabilityEndpointTests
{
    // THE MILESTONE'S REASON FOR EXISTING, asserted at the only layer where "the entire system" means
    // anything.
    //
    // ONE ACCOUNT EXISTS in this host, and it never calls /v1/jobs or /v1/job-offers. That is what makes
    // the emptiness check below exhaustive rather than suggestive: every path that creates a posting —
    // POST /v1/jobs and POST /v1/job-offers/import — makes the CALLER its owner, so a page of the only
    // account's postings is a page of every posting the process can hold. Without that reasoning, "the
    // response was 200" is compatible with a build that quietly required a posting and found one.
    [Fact]
    public async Task Readability_WithNoJobPostingInTheSystem_StillAnswers()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var accountId = await AccountIdOf(client, candidateToken);
        var resumeId = await ImportResumeAsync(client, candidateToken);

        var postings = factory.Services.GetRequiredService<IJobPostingRepository>();
        var page = await postings.GetPageByOwnerIdAsync(
            new AccountId(accountId), PageRequest.Create(limit: 100, cursor: null).Value!);
        page.Items.Should().BeEmpty("this host has never created a job posting");

        var response = await ReadabilityAsync(client, candidateToken, resumeId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("readabilityScore").GetInt32().Should().BeGreaterThan(0);
        json.RootElement.TryGetProperty("jobPostingId", out _).Should().BeFalse(
            "a readability run names no posting, because none took part in it");
    }

    // The write, observed from outside the process. "The response looks right" is true whether or not a
    // row was written, so this reads the store the whole Api suite runs on.
    [Fact]
    public async Task Readability_WritesOneReportPerRequest()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await ImportResumeAsync(client, candidateToken);
        var reports = factory.Services.GetRequiredService<InMemoryReadabilityReportRepository>();

        (await ReadabilityAsync(client, candidateToken, resumeId)).StatusCode.Should().Be(HttpStatusCode.OK);
        reports.Count.Should().Be(1);

        (await ReadabilityAsync(client, candidateToken, resumeId)).StatusCode.Should().Be(HttpStatusCode.OK);
        reports.Count.Should().Be(2, "there is no de-duplication here yet, and a duplicate row is the safe direction");
    }

    // The owner check at the HTTP boundary. The handler returns the literal "Forbidden.", and
    // ResultExtensions is what turns that one string into a 403 rather than the 400 every other failure
    // gets — so the mapping is worth one end-to-end assertion instead of being trusted.
    //
    // A 403 could also come from the AUTHORIZATION POLICY on the group rather than from the ownership
    // check, which would prove nothing about this endpoint. The stranger here is a Candidate — the same
    // role the owner holds, and one the policy admits — and the first assertion below shows that role
    // reading its OWN readability successfully in the same host. So the only thing left to explain the
    // second 403 is the ownership test.
    [Fact]
    public async Task Readability_ForAnotherAccountsResume_Returns403()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, ownerToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, strangerToken) = await client.RegisterAndLoginAsync("stranger@example.com");

        var ownerResumeId = await ImportResumeAsync(client, ownerToken);
        var strangerResumeId = await ImportResumeAsync(client, strangerToken);

        (await ReadabilityAsync(client, strangerToken, strangerResumeId))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the stranger's role is admitted by the policy");

        (await ReadabilityAsync(client, strangerToken, ownerResumeId))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Readability_ForAResumeThatDoesNotExist_Returns404()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await ReadabilityAsync(client, candidateToken, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Readability_WithoutAToken_Returns401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsync($"/v1/resumes/{Guid.NewGuid()}/readability", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // THE RESPONSE SHAPE, pinned field by field so a rename or a dropped mapping is a failure here
    // rather than a client's problem later.
    [Fact]
    public async Task Readability_ReturnsTheDocumentedShapeWithNamedEnumsAndBareGuids()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await ImportResumeAsync(client, candidateToken);

        var response = await ReadabilityAsync(client, candidateToken, resumeId);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.EnumerateObject().Select(property => property.Name).Should().Equal(
            "id", "breakdown", "resumeId", "evaluatedAt", "recommendations", "readabilityScore", "band");

        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.String, "ids are bare guids, not wrappers");
        root.GetProperty("resumeId").GetGuid().Should().Be(resumeId);
        root.GetProperty("band").GetString().Should().BeOneOf("Low", "Medium", "Good", "Strong");

        // NEVER overallScore: that name means "match against this posting" and the two are on the same
        // 0..100 scale, so one name over both is how a client ends up charting them against each other.
        root.TryGetProperty("overallScore", out _).Should().BeFalse();

        var breakdown = root.GetProperty("breakdown");
        breakdown.EnumerateObject().Select(property => property.Name).Should().Equal(
            "completenessScore", "contactScore", "achievementsScore", "chronologyScore",
            "atsParseabilityScore", "weights", "weightedTotal", "sections");

        var weights = breakdown.GetProperty("weights");
        weights.EnumerateObject().Select(property => property.Name).Should().Equal(
            "completeness", "contact", "achievements", "chronology", "atsParseability", "schemaVersion");
        weights.GetProperty("schemaVersion").GetInt32().Should().Be(1);

        var sections = breakdown.GetProperty("sections").EnumerateArray().ToList();
        sections.Select(section => section.GetProperty("section").GetString()).Should().Equal(
            "Completeness", "Contact", "Achievements", "Chronology", "AtsParseability");
    }

    // AtsParseability renormalizes out, over the wire. The evidence it needs does not exist yet, so the
    // section carries no weight and the four that remain still total 1.0 — which is what keeps the
    // ceiling at 100 rather than 90.
    [Fact]
    public async Task Readability_ReportsAtsParseabilityAtZeroWeightAndTheRestStillSumToOne()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await ImportResumeAsync(client, candidateToken);

        var response = await ReadabilityAsync(client, candidateToken, resumeId);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var sections = json.RootElement.GetProperty("breakdown").GetProperty("sections")
            .EnumerateArray()
            .ToDictionary(
                section => section.GetProperty("section").GetString()!,
                section => section.GetProperty("weight").GetDouble());

        sections["AtsParseability"].Should().Be(0.0);
        sections.Values.Sum().Should().BeApproximately(1.0, 1e-9);
    }

    // A CV filled in by the import path scores well and is told so; the advice it does get is
    // actionable and carries a measured impact on the 0..1 scale.
    [Fact]
    public async Task Readability_ReturnsAdviceWithNamedEnumsAndAnImpactOnTheZeroToOneScale()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateBareResumeAsync(client, candidateToken);

        var response = await ReadabilityAsync(client, candidateToken, resumeId);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var advice = json.RootElement.GetProperty("recommendations").EnumerateArray().ToList();
        advice.Should().NotBeEmpty("a resume with nothing but a name and an email has plenty to fix");

        foreach (var entry in advice)
        {
            entry.GetProperty("section").ValueKind.Should().Be(JsonValueKind.String);
            entry.GetProperty("priority").GetString().Should()
                .BeOneOf("Critical", "Important", "NiceToHave");
            entry.GetProperty("kind").ValueKind.Should().Be(JsonValueKind.String);
            entry.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("impact").GetDouble().Should().BeInRange(0.0, 1.0);
        }

        // Priority ascending, then impact descending within a priority — the order re-derived on the way
        // out, because the stored set carries no position.
        var priorities = advice.Select(entry => entry.GetProperty("priority").GetString()).ToList();
        priorities.Should().BeInAscendingOrder(new PriorityOrder());
    }

    // The two totals live side by side and are never blended. Asserted together in one host so the claim
    // is about the same resume rather than about two runs of different things.
    [Fact]
    public async Task Readability_AndScoring_AnswerSeparateNumbersUnderSeparateNames()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await ImportResumeAsync(client, candidateToken);
        var jobId = await ScoringEndpointTests.CreateJobAsync(client, recruiterToken);
        await ScoringEndpointTests.PublishAsync(client, recruiterToken, jobId);

        using var readability = JsonDocument.Parse(
            await (await ReadabilityAsync(client, candidateToken, resumeId)).Content.ReadAsStringAsync());
        using var analysis = JsonDocument.Parse(
            await (await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId))
                .Content.ReadAsStringAsync());

        readability.RootElement.TryGetProperty("overallScore", out _).Should().BeFalse();
        analysis.RootElement.TryGetProperty("readabilityScore", out _).Should().BeFalse();

        readability.RootElement.GetProperty("readabilityScore").GetInt32().Should().BeInRange(0, 100);
        analysis.RootElement.GetProperty("overallScore").GetInt32().Should().BeInRange(0, 100);
    }

    private static async Task<HttpResponseMessage> ReadabilityAsync(
        HttpClient client, string token, Guid resumeId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/resumes/{resumeId}/readability").WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<Guid> AccountIdOf(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/me").WithBearer(token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    // The richest resume the API can build, so every section has something to measure.
    private static async Task<Guid> ImportResumeAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = JsonContent.Create(ResumeImportTests.FullDraft()),
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    // A name and an email and nothing else — the emptiest resume the API can create.
    private static async Task<Guid> CreateBareResumeAsync(HttpClient client, string token) =>
        await ScoringEndpointTests.CreateResumeAsync(client, token);

    // Critical, Important, NiceToHave — the enum's own order, which is what the response is sorted by.
    private sealed class PriorityOrder : IComparer<string?>
    {
        private static readonly string[] Order = ["Critical", "Important", "NiceToHave"];

        public int Compare(string? left, string? right) =>
            Array.IndexOf(Order, left).CompareTo(Array.IndexOf(Order, right));
    }
}

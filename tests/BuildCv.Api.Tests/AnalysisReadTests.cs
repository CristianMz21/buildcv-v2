using System.Net;
using System.Text.Json;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

// GET /scoring/{analysisId} and GET /resumes/{id}/analyses over HTTP — the loop the product is: score,
// act on the advice, score again, read the two side by side.
public sealed class AnalysisReadTests
{
    // The response a candidate reads back must be the response they were given. Asserted as raw JSON
    // equality rather than field by field, because the failure worth catching is a SECOND SHAPE for the
    // same aggregate quietly appearing on the read path — and a per-field assertion cannot see a field
    // that is present on one endpoint and missing on the other.
    [Fact]
    public async Task GetAnalysis_ReturnsByteForByteWhatTheScoreEndpointReturned()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, _, resumeId, jobId) = await ArrangeScorableAsync(client);

        var scored = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        scored.StatusCode.Should().Be(HttpStatusCode.OK);
        var scoredBody = await scored.Content.ReadAsStringAsync();
        var analysisId = IdOf(scoredBody);

        var read = await GetAnalysisAsync(client, candidateToken, analysisId);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await read.Content.ReadAsStringAsync()).Should().Be(scoredBody);
    }

    // The one that matters most on this endpoint, and it needs a store that behaves like a database
    // rather than like a dictionary handing the same object back.
    //
    // Recommendations are persisted as a SET — the table has no Rank column, by design — so a reloaded
    // analysis carries them in whatever order the server chose. The in-memory store returns the very
    // instance it was given, so a live read against it can never tell a sorting mapper from a
    // non-sorting one. This decorator reconstructs the aggregate with the recommendations in exactly
    // the REVERSE of their display order, which is the worst order a real reload could produce, and the
    // response still has to come back sorted.
    [Fact]
    public async Task GetAnalysis_WhenTheStoreReturnsTheRecommendationsScrambled_StillRendersThemSorted()
    {
        using var factory = new ApiTestFactory(configureServices: services =>
            services.AddSingleton<IAnalysisRepository>(_ =>
                new ReversingAnalysisRepository(new Infrastructure.Persistence.InMemoryAnalysisRepository())));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, _, resumeId, jobId) = await ArrangeScorableAsync(client);

        var scored = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        var analysisId = IdOf(await scored.Content.ReadAsStringAsync());

        var read = await GetAnalysisAsync(client, candidateToken, analysisId);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var recommendations = json.RootElement.GetProperty("recommendations").EnumerateArray().ToList();

        // Without this guard the two assertions below would pass on a single recommendation — or on
        // none — whatever the mapper did with the order, because reversing a one-element list is the
        // identity. This request produces three.
        recommendations.Should().HaveCountGreaterThan(1,
            "reversing fewer than two recommendations cannot scramble anything");

        recommendations.Select(r => r.GetProperty("kind").GetString()).Should().Equal(
            "NoEducationRecorded", "FewerCertificationsThanExpected", "FewerProjectsThanExpected");
        recommendations.Select(r => r.GetProperty("impact").GetDouble()).Should().BeInDescendingOrder();
    }

    // An analysis has no owner column, so this is the request that proves the second read — the one that
    // loads the resume — actually gates. It is also the whole reason /scoring/{id} cannot simply trust
    // an id the caller happens to know.
    //
    // The stranger is the recruiter this scenario already registered, not a third account, because the
    // auth rate limiter allows five requests a minute per client and one register plus one login for
    // each of two accounts already spends four of them.
    //
    // The DETAIL is asserted, not just the status. /scoring carries no role policy — it inherits the
    // fallback, which only requires authentication — so a 403 here can only be the handler's
    // "Forbidden.", and reading the body is what says so rather than a bare status that an authorization
    // filter could equally have produced.
    [Fact]
    public async Task GetAnalysis_ForSomebodyElsesAnalysis_Returns403()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, recruiterToken, resumeId, jobId) = await ArrangeScorableAsync(client);

        var scored = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        var analysisId = IdOf(await scored.Content.ReadAsStringAsync());

        var read = await GetAnalysisAsync(client, recruiterToken, analysisId);

        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        read.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await DetailOf(read)).Should().Be("Forbidden.");
    }

    // 404, not 400. The handler's message ends in "not found." and ResultExtensions routes on that exact
    // suffix — a message ending any other way would report a malformed request instead of a missing row.
    [Fact]
    public async Task GetAnalysis_ForAnIdThatWasNeverScored_Returns404()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var read = await GetAnalysisAsync(client, token, Guid.NewGuid());

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // OLDEST FIRST over HTTP, walked the way a client walks it: three scores of one resume, one page at
    // a time, and the cursor moves FORWARD IN TIME. Every other paged list in this API runs the other
    // way, which is exactly why this one is asserted end to end rather than only at the handler.
    [Fact]
    public async Task GetAnalyses_WalkedByCursor_ReplaysTheHistoryOldestFirst()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, _, resumeId, jobId) = await ArrangeScorableAsync(client);

        var scored = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var response = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            scored.Add(IdOf(await response.Content.ReadAsStringAsync()));
        }

        var visited = new List<Guid>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await GetHistoryPageAsync(client, candidateToken, resumeId, limit: 2, cursor);
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items);
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 1);
        visited.Should().Equal(scored, "a score history is read forwards, from the first run");
    }

    // Each entry is the same shape /scoring/score answered with, which is what makes "did my edit help"
    // a comparison the client can just do rather than a mapping exercise.
    [Fact]
    public async Task GetAnalyses_ReturnsEntriesInTheSameShapeAsTheScoreEndpoint()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, _, resumeId, jobId) = await ArrangeScorableAsync(client);

        var scored = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        using var scoredJson = JsonDocument.Parse(await scored.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/resumes/{resumeId}/analyses")
            .WithBearer(candidateToken);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var historyJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = historyJson.RootElement.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;

        entry.GetRawText().Should().Be(scoredJson.RootElement.GetRawText());

        // The zero-weight signal survives the read path, which is the fact the XML docs and the endpoint
        // description promise a client developer they can rely on. This posting states no skill and no
        // language requirement, so those two sections were not asked about and the remaining four carry
        // the whole score.
        var weights = entry.GetProperty("breakdown").GetProperty("weights");
        weights.GetProperty("skills").GetDouble().Should().Be(0.0);
        weights.GetProperty("languages").GetDouble().Should().Be(0.0);
        new[] { "skills", "experience", "education", "certifications", "projects", "languages" }
            .Sum(name => weights.GetProperty(name).GetDouble())
            .Should().BeApproximately(1.0, 1e-9, "the asked-about sections are renormalized to a ceiling of 1.0");
    }

    // The recruiter is a stranger to this resume but NOT to the route: AuthorizationPolicies.Candidate
    // admits "Candidate", "Recruiter" and "Admin", so the request reaches the handler and the 403 is the
    // ownership check rather than the group's policy turning it away at the door.
    //
    // The DETAIL is what says so, and the distinction was measured rather than assumed: a request
    // rejected by an authorization policy (a Candidate posting to the Recruiter-only /jobs) comes back
    // 403 with NO content type and an EMPTY body, so this assertion cannot even parse a response the
    // policy produced. Only the Result convention puts "Forbidden." in a ProblemDetails detail.
    [Fact]
    public async Task GetAnalyses_ForSomebodyElsesResume_Returns403()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, recruiterToken, resumeId, jobId) = await ArrangeScorableAsync(client);
        await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/resumes/{resumeId}/analyses")
            .WithBearer(recruiterToken);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOf(response)).Should().Be("Forbidden.");
    }

    // A cursor the client invented becomes an ordinary ProblemDetails 400 through the Result convention,
    // not a 500 and not a silent restart of the walk from the first run.
    [Fact]
    public async Task GetAnalyses_WithACursorTheClientInvented_IsABadRequest()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, _, resumeId, jobId) = await ArrangeScorableAsync(client);
        await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/resumes/{resumeId}/analyses?limit=2&cursor=nonsense").WithBearer(candidateToken);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    // Four auth requests — two registers and two logins — which is every slot but one of the 5/min auth
    // window a TestServer client gets, because Connection.RemoteIpAddress is never populated and every
    // request therefore lands in the same "unknown" partition. A third account would 429 rather than
    // fail the assertion it was added for, so tests that need a stranger reuse the recruiter.
    private static async Task<(string CandidateToken, string RecruiterToken, Guid ResumeId, Guid JobId)>
        ArrangeScorableAsync(HttpClient client)
    {
        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await ScoringEndpointTests.CreateResumeAsync(client, candidateToken);
        var jobId = await ScoringEndpointTests.CreateJobAsync(client, recruiterToken);
        await ScoringEndpointTests.PublishAsync(client, recruiterToken, jobId);

        return (candidateToken, recruiterToken, resumeId, jobId);
    }

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("detail").GetString();
    }

    private static async Task<HttpResponseMessage> GetAnalysisAsync(
        HttpClient client, string token, Guid analysisId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/scoring/{analysisId}").WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<(List<Guid> Items, string? NextCursor)> GetHistoryPageAsync(
        HttpClient client, string token, Guid resumeId, int limit, string? cursor)
    {
        var url = cursor is null
            ? $"/resumes/{resumeId}/analyses?limit={limit}"
            : $"/resumes/{resumeId}/analyses?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url).WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetProperty("value").GetGuid())
            .ToList();

        var nextCursor = json.RootElement.GetProperty("nextCursor");
        return (items, nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString());
    }

    private static Guid IdOf(string analysisJson)
    {
        using var json = JsonDocument.Parse(analysisJson);
        return json.RootElement.GetProperty("id").GetProperty("value").GetGuid();
    }

    // Stands in for the one thing the in-memory store cannot reproduce: a database handing back an owned
    // collection in an order nobody chose. Reversing the display order is the strongest form of that —
    // any read path relying on the order it stored is guaranteed to be wrong.
    private sealed class ReversingAnalysisRepository(IAnalysisRepository inner) : IAnalysisRepository
    {
        public Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default) =>
            inner.AddAsync(analysis, cancellationToken);

        public async Task<Analysis?> GetByIdAsync(AnalysisId id, CancellationToken cancellationToken = default) =>
            Reversed(await inner.GetByIdAsync(id, cancellationToken));

        public async Task<Page<Analysis>> GetPageByResumeIdAsync(
            ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default)
        {
            var found = await inner.GetPageByResumeIdAsync(resumeId, page, cancellationToken);
            return new Page<Analysis>([.. found.Items.Select(analysis => Reversed(analysis)!)], found.NextCursor);
        }

        private static Analysis? Reversed(Analysis? analysis) =>
            analysis is null
                ? null
                : Analysis.Create(
                    analysis.Id,
                    analysis.Breakdown,
                    analysis.ResumeId,
                    analysis.JobPostingId,
                    analysis.ScoredAt,
                    [.. RecommendationOrder.Sort(analysis.Recommendations).Reverse()]);
    }
}

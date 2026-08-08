using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Jobs;
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
    //
    // The DETAIL is asserted too, because the status alone cannot tell "Analysis not found." from
    // "Resume not found." and that is exactly the distinction the deleted-resume test below turns on.
    [Fact]
    public async Task GetAnalysis_ForAnIdThatWasNeverScored_Returns404()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var read = await GetAnalysisAsync(client, token, Guid.NewGuid());

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DetailOf(read)).Should().Be("Analysis not found.");
    }

    // THE PROVIDER-PARITY TEST, and it runs through the composed in-memory provider the whole Api suite
    // sits on rather than through a hand-written fake.
    //
    // Deleting a resume must hide every score derived from it. Under EF that is
    // ResumeRepository.CascadeToAnalysesAsync tombstoning the analyses in the same unit of work, so the
    // global query filter makes them vanish at the first read. InMemoryResumeRepository.DeleteAsync now
    // mirrors it by dropping those rows, so both providers miss at the SAME read.
    //
    // THAT WAS NOT TRUE WHEN THIS TEST WAS WRITTEN. The in-memory store cascaded nothing, the analysis
    // survived its resume, and the miss happened one read later — GetAnalysisByIdHandler turning a
    // missing resume into "Analysis not found." That equivalence is what this test pinned, and it is
    // exactly why issue #18 called the parity a property of the HANDLERS rather than of the store: the
    // next handler to read an analysis without loading its resume first would have reintroduced the
    // divergence, and the Api suite runs on this store, so it would have certified it green.
    //
    // The test is unchanged and still passes, which is the useful part — the observable was already the
    // contract, and closing the store's gap did not move it. GetAnalysisByIdHandlerTests still covers
    // the orphan branch against fakes, where an orphan can still be constructed.
    //
    // Costs no auth budget — the delete and the two reads reuse the candidate's existing token.
    [Fact]
    public async Task GetAnalysis_AfterTheResumeWasDeleted_Answers404OnBothTheScoreAndItsHistory()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, _, resumeId, jobId) = await ArrangeScorableAsync(client);

        var scored = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        var analysisId = IdOf(await scored.Content.ReadAsStringAsync());
        (await GetAnalysisAsync(client, candidateToken, analysisId)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the score has to be readable first, or the 404 below proves nothing");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/v1/resumes/{resumeId}")
            .WithBearer(candidateToken);
        (await client.SendAsync(delete)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var readScore = await GetAnalysisAsync(client, candidateToken, analysisId);
        readScore.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DetailOf(readScore)).Should().Be("Analysis not found.",
            "the caller named an analysis, so it is told about an analysis");

        using var readHistory = new HttpRequestMessage(HttpMethod.Get, $"/v1/resumes/{resumeId}/analyses")
            .WithBearer(candidateToken);
        var history = await client.SendAsync(readHistory);
        history.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DetailOf(history)).Should().Be("Resume not found.",
            "this route names a resume, and that resume is the thing that is gone");
    }

    // Anonymous reads are refused. The /scoring group carries no RequireAuthorization of its own and
    // leans on the fallback policy Program.cs sets, and nothing asserted the outcome until now.
    //
    // WHAT THIS TEST DOES NOT DO, stated because the obvious stronger claim is false and was measured to
    // be false: it does not isolate the fallback policy. This endpoint is gated TWICE. Adding
    // AllowAnonymous to the group still answers 401, because the handler lambda calls
    // ClaimsPrincipalExtensions.GetAccountId, which throws UnauthorizedAccessException on a principal
    // with no `sub` claim, and DomainExceptionHandler maps that to 401. So a stray AllowAnonymous here
    // would NOT open stored analyses — a useful thing to know, and the reason this test cannot be sold
    // as the guard against one.
    //
    // The two 401s are distinguishable, just not durably: the policy rejection carries
    // "type":"about:blank" (ASP.NET filling in a status-code ProblemDetails) and the exception handler's
    // does not, because it constructs its own. Asserting on that would couple this test to an ASP.NET
    // detail and break the day somebody sets Type on the handler, so it is recorded here rather than
    // asserted. Needs no auth-window budget: it sends no credential at all.
    [Fact]
    public async Task GetAnalysis_WithNoCredential_Returns401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync($"/v1/scoring/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // OLDEST FIRST over HTTP, walked the way a client walks it: three scores of one resume, one page at
    // a time, and the cursor moves FORWARD IN TIME. Every other paged list in this API runs the other
    // way, which is exactly why this one is asserted end to end rather than only at the handler.
    //
    // THE RESUME IS EDITED BETWEEN SCORES, and it has to be. Three identical requests no longer produce
    // three rows — ScoreResumeHandler returns the stored analysis when the resume, the posting, the model
    // version and the day all match — so a loop that only re-posted would leave one history entry and
    // this test would be walking a page it wrote by accident. Adding a skill bumps Resume.UpdatedAt,
    // which is what makes each run a distinct scoring EVENT rather than a button press. The distinct ids
    // asserted below are the proof that it worked.
    [Fact]
    public async Task GetAnalyses_WalkedByCursor_ReplaysTheHistoryOldestFirst()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, _, resumeId, jobId) = await ArrangeScorableAsync(client);

        var scored = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            if (index > 0)
                await AddSkillAsync(client, candidateToken, resumeId, $"Skill{index}");

            var response = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            scored.Add(IdOf(await response.Content.ReadAsStringAsync()));
        }

        scored.Should().OnlyHaveUniqueItems("each edit makes the next score a new event, not a repeat");

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

    // DE-DUPLICATION over HTTP, and the assertion is the HISTORY rather than the response.
    //
    // Both requests answer 200 with the same numbers whether the second one reused the stored analysis or
    // wrote a second identical row, so comparing the two bodies proves nothing on its own. What separates
    // the two worlds is how many entries the candidate's score history then has — one scoring event, not
    // two button presses — and the shared id says which row they were both handed.
    //
    // Through the composed in-memory store the whole Api suite runs on, so this also pins that
    // InMemoryAnalysisRepository.GetLatestByPairAsync agrees with the EF one; the SQL Server half is
    // AnalysisRepositoryTests.GetLatestByPairAsync_ReturnsTheNewestRowForThatPairOnly.
    [Fact]
    public async Task Score_TwiceWithNothingChanged_ReturnsTheSameAnalysisAndLeavesOneHistoryEntry()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, _, resumeId, jobId) = await ArrangeScorableAsync(client);

        var first = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        IdOf(await second.Content.ReadAsStringAsync())
            .Should().Be(IdOf(await first.Content.ReadAsStringAsync()));

        var history = await GetHistoryPageAsync(client, candidateToken, resumeId, limit: 10, cursor: null);
        history.Items.Should().ContainSingle("a re-score with nothing changed is not a second scoring event");

        // And an edit still starts a new one, so this is de-duplication rather than a resume being
        // scoreable only once.
        await AddSkillAsync(client, candidateToken, resumeId, "Kubernetes");
        var third = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        third.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetHistoryPageAsync(client, candidateToken, resumeId, limit: 10, cursor: null))
            .Items.Should().HaveCount(2);
    }

    // STALENESS ON THE WIRE, over the whole loop the product is: score, read it back, edit the CV, read
    // it back again.
    //
    // The same analysis id is read three times and the row never changes — only the resume beside it
    // does — which is what makes this an assertion about a value computed at read time rather than one
    // stored on the score. A persisted flag would answer `false` on the third read.
    //
    // The history is checked in the same request budget, because it is where the flag earns its keep: an
    // older entry is stale and the run taken against the current CV is not, so a client can render the
    // list without comparing anything itself.
    [Fact]
    public async Task GetAnalysis_AfterTheResumeIsEdited_ReportsTheStoredScoreAsStale()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (candidateToken, _, resumeId, jobId) = await ArrangeScorableAsync(client);

        var scored = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        scored.StatusCode.Should().Be(HttpStatusCode.OK);
        var analysisId = IdOf(await scored.Content.ReadAsStringAsync());

        (await IsStaleAsync(client, candidateToken, analysisId)).Should().BeFalse(
            "nothing has changed since the score was taken");

        await AddSkillAsync(client, candidateToken, resumeId, "Terraform");

        (await IsStaleAsync(client, candidateToken, analysisId)).Should().BeTrue(
            "the score now describes a CV the candidate no longer has");

        var rescored = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        rescored.StatusCode.Should().Be(HttpStatusCode.OK);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/resumes/{resumeId}/analyses")
            .WithBearer(candidateToken);
        var history = await client.SendAsync(request);
        history.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("items").EnumerateArray()
            .Select(entry => entry.GetProperty("isStale").GetBoolean())
            .Should().Equal(new[] { true, false },
                "the history is oldest first, so the run that predates the edit is the stale one");
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

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/resumes/{resumeId}/analyses")
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

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/resumes/{resumeId}/analyses")
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
            HttpMethod.Get, $"/v1/resumes/{resumeId}/analyses?limit=2&cursor=nonsense").WithBearer(candidateToken);
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

    // The cheapest real edit to a resume over HTTP. It matters only that it goes through a Domain mutator,
    // because every one of them calls Touch() and that is what moves UpdatedAt.
    internal static async Task AddSkillAsync(HttpClient client, string token, Guid resumeId, string skillName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/resumes/{resumeId}/skills")
        {
            Content = JsonContent.Create(new { skillName, level = (string?)null, yearsOfExperience = (int?)null })
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<bool> IsStaleAsync(HttpClient client, string token, Guid analysisId)
    {
        var response = await GetAnalysisAsync(client, token, analysisId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("isStale").GetBoolean();
    }

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("detail").GetString();
    }

    private static async Task<HttpResponseMessage> GetAnalysisAsync(
        HttpClient client, string token, Guid analysisId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/scoring/{analysisId}").WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<(List<Guid> Items, string? NextCursor)> GetHistoryPageAsync(
        HttpClient client, string token, Guid resumeId, int limit, string? cursor)
    {
        var url = cursor is null
            ? $"/v1/resumes/{resumeId}/analyses?limit={limit}"
            : $"/v1/resumes/{resumeId}/analyses?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url).WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToList();

        var nextCursor = json.RootElement.GetProperty("nextCursor");
        return (items, nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString());
    }

    private static Guid IdOf(string analysisJson)
    {
        using var json = JsonDocument.Parse(analysisJson);
        return json.RootElement.GetProperty("id").GetGuid();
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

        public async Task<Analysis?> GetLatestByPairAsync(
            ResumeId resumeId, JobPostingId jobPostingId, CancellationToken cancellationToken = default) =>
            Reversed(await inner.GetLatestByPairAsync(resumeId, jobPostingId, cancellationToken));

        public async Task<Page<Analysis>> GetPageByResumeIdAsync(
            ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default)
        {
            var found = await inner.GetPageByResumeIdAsync(resumeId, page, cancellationToken);
            return new Page<Analysis>([.. found.Items.Select(analysis => Reversed(analysis)!)], found.NextCursor);
        }

        // The provenance is CARRIED THROUGH, and forgetting to would be invisible in the assertion this
        // decorator exists for. Rebuilding the aggregate without it leaves both timestamps null, which
        // reads as "unknown, therefore stale" — so every response through this store would report a stale
        // score and no re-score would ever de-duplicate, while the recommendation ORDER this class is
        // about stayed perfectly correct.
        private static Analysis? Reversed(Analysis? analysis) =>
            analysis is null
                ? null
                : Analysis.Create(
                    analysis.Id,
                    analysis.Breakdown,
                    analysis.ResumeId,
                    analysis.JobPostingId,
                    analysis.ScoredAt,
                    [.. RecommendationOrder.Sort(analysis.Recommendations).Reverse()],
                    analysis.ResumeUpdatedAt,
                    analysis.JobPostingUpdatedAt);
    }
}

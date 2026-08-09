using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

// GET /v1/readability/{reportId} and GET /v1/resumes/{id}/readability over HTTP — the loop the second
// half of the product is: evaluate, act on the advice, evaluate again, read the two side by side.
//
// Before these routes existed a readability report could be created and never read again: the write was
// observable only from inside the process, through InMemoryReadabilityReportRepository.Count.
public sealed class ReadabilityReadTests
{
    // The response a candidate reads back must be the response they were given. Asserted as raw JSON
    // equality rather than field by field, because the failure worth catching is a SECOND SHAPE for the
    // same aggregate quietly appearing on the read path — and a per-field assertion cannot see a field
    // that is present on one endpoint and missing on the other.
    [Fact]
    public async Task GetReadability_ReturnsByteForByteWhatTheEvaluateEndpointReturned()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (token, resumeId) = await ArrangeAsync(client);

        var evaluated = await EvaluateAsync(client, token, resumeId);
        evaluated.StatusCode.Should().Be(HttpStatusCode.OK);
        var evaluatedBody = await evaluated.Content.ReadAsStringAsync();

        var read = await GetReportAsync(client, token, IdOf(evaluatedBody));

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await read.Content.ReadAsStringAsync()).Should().Be(evaluatedBody);
    }

    // A report has no owner column, so this is the request that proves the second read — the one that
    // loads the resume — actually gates, and it is the whole reason /readability/{id} cannot simply
    // trust an id the caller happens to know.
    //
    // The stranger is a Candidate, the same role the owner holds and one the fallback policy admits, and
    // the DETAIL is asserted rather than the bare status: /readability carries no role policy, so a 403
    // with "Forbidden." in a ProblemDetails body can only be the handler's ownership check. A rejection
    // by an authorization policy answers 403 with an empty body and no content type.
    [Fact]
    public async Task GetReadability_ForSomebodyElsesReport_Returns403()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (ownerToken, resumeId) = await ArrangeAsync(client);
        var (_, strangerToken) = await client.RegisterAndLoginAsync("stranger@example.com");

        var evaluated = await EvaluateAsync(client, ownerToken, resumeId);
        var reportId = IdOf(await evaluated.Content.ReadAsStringAsync());

        var read = await GetReportAsync(client, strangerToken, reportId);

        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        read.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await DetailOf(read)).Should().Be("Forbidden.");
    }

    // 404, not 400. The handler's message ends in "not found." and ResultExtensions routes on that exact
    // suffix — a message ending any other way would report a malformed request instead of a missing row.
    // The DETAIL is asserted because the status alone cannot tell this from "Resume not found.", which
    // is the distinction the deleted-resume test below turns on.
    [Fact]
    public async Task GetReadability_ForAnIdThatWasNeverEvaluated_Returns404()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var read = await GetReportAsync(client, token, Guid.NewGuid());

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DetailOf(read)).Should().Be("Readability report not found.");
    }

    [Fact]
    public async Task GetReadability_WithNoCredential_Returns401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync($"/v1/readability/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Deleting a resume must take every report derived from it out of every read, and the promise is
    // sharper here than for scoring: a readability recommendation quotes the candidate's own bullet
    // points and job titles. Under EF that is ResumeRepository.CascadeToReadabilityReportsAsync
    // tombstoning them in the same unit of work; InMemoryResumeRepository.DeleteAsync drops them.
    //
    // THE TWO 404s DO NOT PROVE THE CASCADE, and it is worth saying so rather than letting the test name
    // imply it. Measured: with the in-memory cascade removed, both of them still answer exactly the same
    // — GetReadabilityReportByIdHandler loads the resume to authorize and turns a missing one into
    // "Readability report not found.", and the history route never reaches its store at all. Two causes,
    // one observable, which is the failure mode that lets a store diverge from production while the Api
    // suite stays green.
    //
    // THE COUNT IS THE ASSERTION THAT SEPARATES THEM. One account, one resume, one run, so the store's
    // total is this resume's history — and it goes to zero only if something actually removed the row.
    // Removing the cascade turns that line red and leaves the rest of this test passing.
    //
    // The two messages are asserted all the same: they differ on purpose — this route names a report,
    // the history names the resume — so the pair also pins that neither 404 is a routing miss.
    [Fact]
    public async Task GetReadability_AfterTheResumeWasDeleted_IsGoneFromTheStoreAndAnswers404()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (token, resumeId) = await ArrangeAsync(client);
        var reports = factory.Services.GetRequiredService<InMemoryReadabilityReportRepository>();

        var evaluated = await EvaluateAsync(client, token, resumeId);
        var reportId = IdOf(await evaluated.Content.ReadAsStringAsync());
        (await GetReportAsync(client, token, reportId)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the report has to be readable first, or the 404 below proves nothing");
        reports.Count.Should().Be(1, "or the zero below would be true before the delete as well");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/v1/resumes/{resumeId}")
            .WithBearer(token);
        (await client.SendAsync(delete)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        reports.Count.Should().Be(0, "deleting a resume removes the reports derived from it, not merely hides them");

        var readReport = await GetReportAsync(client, token, reportId);
        readReport.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DetailOf(readReport)).Should().Be("Readability report not found.",
            "the caller named a report, so it is told about a report");

        using var readHistory = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/resumes/{resumeId}/readability").WithBearer(token);
        var history = await client.SendAsync(readHistory);
        history.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DetailOf(history)).Should().Be("Resume not found.",
            "this route names a resume, and that resume is the thing that is gone");
    }

    // OLDEST FIRST over HTTP, walked the way a client walks it: three runs of one resume, one page at a
    // time, and the cursor moves FORWARD IN TIME. Every paged list in this API except score history runs
    // the other way, which is exactly why this one is asserted end to end rather than only at the
    // handler.
    //
    // Three plain POSTs are enough, unlike the scoring history: readability has no de-duplication, so
    // each request is its own run. The distinct ids assert that rather than assuming it.
    [Fact]
    public async Task GetReadabilityHistory_WalkedByCursor_ReplaysTheRunsOldestFirst()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (token, resumeId) = await ArrangeAsync(client);

        var evaluated = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var response = await EvaluateAsync(client, token, resumeId);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            evaluated.Add(IdOf(await response.Content.ReadAsStringAsync()));
        }

        evaluated.Should().OnlyHaveUniqueItems("every request writes its own run; there is no de-duplication here");

        var visited = new List<Guid>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await GetHistoryPageAsync(client, token, resumeId, limit: 2, cursor);
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items);
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 1);
        visited.Should().Equal(evaluated, "a readability history is read forwards, from the first run");
    }

    // Each entry is the same shape POST /v1/resumes/{id}/readability answered with, which is what makes
    // "did acting on that advice pay what its measured impact promised" a comparison the client can just
    // do rather than a mapping exercise.
    [Fact]
    public async Task GetReadabilityHistory_ReturnsEntriesInTheSameShapeAsTheEvaluateEndpoint()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (token, resumeId) = await ArrangeAsync(client);

        var evaluated = await EvaluateAsync(client, token, resumeId);
        using var evaluatedJson = JsonDocument.Parse(await evaluated.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/resumes/{resumeId}/readability").WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var historyJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = historyJson.RootElement.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;

        entry.GetRawText().Should().Be(evaluatedJson.RootElement.GetRawText());

        // The advice is not a summary that lost its recommendations on the way through the paged
        // mapper — an easy thing to drop, and one no assertion about the score would notice.
        entry.GetProperty("recommendations").EnumerateArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetReadabilityHistory_ForSomebodyElsesResume_Returns403()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (ownerToken, resumeId) = await ArrangeAsync(client);
        var (_, strangerToken) = await client.RegisterAndLoginAsync("stranger@example.com");
        await EvaluateAsync(client, ownerToken, resumeId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/resumes/{resumeId}/readability").WithBearer(strangerToken);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOf(response)).Should().Be("Forbidden.");
    }

    // A cursor the client invented becomes an ordinary ProblemDetails 400 through the Result convention,
    // not a 500 and not a silent restart of the walk from the first run.
    [Fact]
    public async Task GetReadabilityHistory_WithACursorTheClientInvented_IsABadRequest()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (token, resumeId) = await ArrangeAsync(client);
        await EvaluateAsync(client, token, resumeId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/resumes/{resumeId}/readability?limit=2&cursor=nonsense").WithBearer(token);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    // One register and one login — two of the five slots a TestServer client gets in the 5/min auth
    // window, because Connection.RemoteIpAddress is never populated and every request lands in the same
    // "unknown" partition. Tests that also need a stranger spend two more and stay under the ceiling.
    private static async Task<(string Token, Guid ResumeId)> ArrangeAsync(HttpClient client)
    {
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = JsonContent.Create(ResumeImportTests.FullDraft()),
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (token, json.RootElement.GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> EvaluateAsync(
        HttpClient client, string token, Guid resumeId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/resumes/{resumeId}/readability").WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetReportAsync(
        HttpClient client, string token, Guid reportId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/readability/{reportId}")
            .WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<(List<Guid> Items, string? NextCursor)> GetHistoryPageAsync(
        HttpClient client, string token, Guid resumeId, int limit, string? cursor)
    {
        var url = cursor is null
            ? $"/v1/resumes/{resumeId}/readability?limit={limit}"
            : $"/v1/resumes/{resumeId}/readability?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";

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

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("detail").GetString();
    }

    private static Guid IdOf(string readabilityJson)
    {
        using var json = JsonDocument.Parse(readabilityJson);
        return json.RootElement.GetProperty("id").GetGuid();
    }
}

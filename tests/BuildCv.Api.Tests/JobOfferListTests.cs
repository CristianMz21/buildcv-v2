using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// GET /v1/job-offers over HTTP — the read that makes POST /v1/job-offers/import more than a one-shot.
//
// Before this route existed, a candidate who imported an offer could find it again only if they had
// kept the Location header from the 201: IJobPostingRepository.GetPageByOwnerIdAsync was implemented in
// both stores and called by nothing.
public sealed class JobOfferListTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Newest first, walked one page at a time. Every other assertion in this file rests on the list
    // being complete and in order, so it is the first thing pinned.
    [Fact]
    public async Task GetJobOffers_WalkedByCursor_ReturnsEveryImportedOfferNewestFirst()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var imported = new List<Guid>();
        for (var index = 0; index < 3; index++)
            imported.Add(await ImportOfferAsync(client, token, $"Role {index}"));

        var visited = new List<Guid>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await GetPageAsync(client, token, limit: 2, cursor);
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items);
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 1);
        imported.Reverse();
        visited.Should().Equal(imported, "an inventory of what the candidate is chasing reads newest first");
    }

    // THE ISOLATION, over HTTP and through the composed store. A Draft offer names the opportunity a
    // candidate is chasing, so a missing owner filter here would publish that to whoever asked first.
    //
    // The stranger's own list is read in the same request budget, which is what stops this passing
    // because the route returns nothing at all to anybody.
    [Fact]
    public async Task GetJobOffers_ReturnsOnlyTheCallersOwnOffers()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, mine) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, theirs) = await client.RegisterAndLoginAsync("stranger@example.com");

        var myOffer = await ImportOfferAsync(client, mine, "My Opportunity");
        var theirOffer = await ImportOfferAsync(client, theirs, "Their Opportunity");

        var myList = await GetPageAsync(client, mine, limit: 50, cursor: null);
        var theirList = await GetPageAsync(client, theirs, limit: 50, cursor: null);

        myList.Items.Should().ContainSingle().Which.Should().Be(myOffer);
        theirList.Items.Should().ContainSingle("or the single result above would prove nothing")
            .Which.Should().Be(theirOffer);
    }

    // OWNERSHIP, NOT PROVENANCE — the decision on GetJobPostingsByOwnerHandler, asserted end to end.
    //
    // The recruiter both creates a posting at POST /v1/jobs and imports an offer at POST
    // /v1/job-offers/import. Nothing on either row records which route wrote it, so this route answers
    // with both — and the two are deliberately distinguishable by `status` after the publish, which is
    // exactly the proxy a narrower "imported only" filter would have reached for.
    [Fact]
    public async Task GetJobOffers_ForARecruiterWhoAlsoCreatedAPosting_ListsBoth()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, recruiter) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var importedOffer = await ImportOfferAsync(client, recruiter, "Imported Offer");

        using var create = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs")
        {
            Content = JsonContent.Create(new
            {
                title = "Recruiter Posting",
                companyName = "Contoso",
                companyId = (Guid?)null,
                description = "Build deterministic scoring systems."
            })
        }.WithBearer(recruiter);

        var created = await client.SendAsync(create);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdId = IdOf(await created.Content.ReadAsStringAsync());

        using var publish = new HttpRequestMessage(HttpMethod.Post, $"/v1/jobs/{createdId}/publish")
            .WithBearer(recruiter);
        (await client.SendAsync(publish)).StatusCode.Should().Be(HttpStatusCode.OK);

        var listed = await GetEntriesAsync(client, recruiter);

        listed.Select(entry => entry.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo(new[] { importedOffer, createdId });

        listed.Select(entry => entry.GetProperty("status").GetString())
            .Should().BeEquivalentTo(new[] { "Draft", "Published" },
                "the two are separable by status, and this route returns them both anyway");
    }

    // The entry a client renders is the same shape GET /v1/jobs/{id} answers, so one renderer serves
    // both. Asserted as raw JSON equality, because the failure worth catching is a SECOND SHAPE for one
    // aggregate appearing on the list path — which a per-field assertion cannot see.
    [Fact]
    public async Task GetJobOffers_ReturnsEntriesInTheSameShapeAsTheJobEndpoint()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var offerId = await ImportOfferAsync(client, token, "Senior Backend Engineer");

        using var byId = new HttpRequestMessage(HttpMethod.Get, $"/v1/jobs/{offerId}").WithBearer(token);
        var single = await client.SendAsync(byId);
        single.StatusCode.Should().Be(HttpStatusCode.OK);
        using var singleJson = JsonDocument.Parse(await single.Content.ReadAsStringAsync());

        var entry = (await GetEntriesAsync(client, token)).Should().ContainSingle().Subject;

        entry.GetRawText().Should().Be(singleJson.RootElement.GetRawText());
    }

    [Fact]
    public async Task GetJobOffers_WithNoOffersImported_IsAnEmptyFinalPage()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var page = await GetPageAsync(client, token, limit: 50, cursor: null);

        page.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    // A cursor the client invented becomes an ordinary ProblemDetails 400 through the Result convention,
    // not a 500 and not a silent restart of the walk from the top.
    [Fact]
    public async Task GetJobOffers_WithACursorTheClientInvented_IsABadRequest()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        await ImportOfferAsync(client, token, "Senior Backend Engineer");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/v1/job-offers?limit=2&cursor=nonsense").WithBearer(token);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task GetJobOffers_WithNoCredential_Returns401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync("/v1/job-offers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<Guid> ImportOfferAsync(HttpClient client, string token, string title)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/job-offers/import")
        {
            Content = JsonContent.Create(
                new
                {
                    title,
                    companyName = "Contoso",
                    requirements = new[] { new { skill = "C#", priority = "MustHave" } }
                },
                options: Web)
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return IdOf(await response.Content.ReadAsStringAsync());
    }

    private static async Task<(List<Guid> Items, string? NextCursor)> GetPageAsync(
        HttpClient client, string token, int limit, string? cursor)
    {
        var url = cursor is null
            ? $"/v1/job-offers?limit={limit}"
            : $"/v1/job-offers?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";

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

    private static async Task<List<JsonElement>> GetEntriesAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/job-offers?limit=50").WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. json.RootElement.GetProperty("items").EnumerateArray().Select(entry => entry.Clone())];
    }

    private static Guid IdOf(string postingJson)
    {
        using var json = JsonDocument.Parse(postingJson);
        return json.RootElement.GetProperty("id").GetGuid();
    }
}

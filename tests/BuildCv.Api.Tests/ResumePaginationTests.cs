using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// GET /resumes over HTTP, end to end: the query string binds, the cursor survives a round trip through
// JSON, and a cursor the client made up comes back as a ProblemDetails 400 rather than an unhandled
// exception or a silent first page.
//
// This runs against the in-memory provider, which is why InMemoryResumeRepository carries an insertion
// counter: without it these assertions would be about dictionary iteration order and would say nothing
// about production.
public sealed class ResumePaginationTests
{
    [Fact]
    public async Task GetResumes_WalkedByCursor_ReturnsEveryResumeOnceNewestFirst()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var created = new List<Guid>();
        for (var index = 0; index < 5; index++)
            created.Add(await CreateResumeAsync(client, token, $"Candidate {index}"));
        created.Reverse();

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

        pageSizes.Should().Equal(2, 2, 1);
        visited.Should().Equal(created);
    }

    [Fact]
    public async Task GetResumes_WithNoQueryString_ReturnsTheFirstPageAndNoCursor()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        await CreateResumeAsync(client, token, "Only Resume");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/resumes").WithBearer(token);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await ReadPageAsync(response);
        page.Items.Should().ContainSingle();
        page.NextCursor.Should().BeNull();
    }

    // The failure mode this whole task exists to avoid: a hostile cursor has to become an ordinary 400
    // through the Result convention, not a 500 and not a full unfiltered read served as page one.
    [Theory]
    [InlineData("nonsense")]
    [InlineData("AAAAAAAAAAA")]
    [InlineData("%27%20OR%201%3D1%20--")]
    public async Task GetResumes_WithACursorTheClientInvented_IsABadRequest(string cursor)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        await CreateResumeAsync(client, token, "Only Resume");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/resumes?limit=2&cursor={cursor}")
            .WithBearer(token);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task GetResumes_WithALimitBeyondTheCeiling_ClampsRatherThanFailing()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        await CreateResumeAsync(client, token, "Only Resume");

        var page = await GetPageAsync(client, token, limit: 100_000, cursor: null);

        page.Items.Should().ContainSingle();
        page.NextCursor.Should().BeNull();
    }

    private static async Task<Guid> CreateResumeAsync(HttpClient client, string token, string fullName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes")
        {
            Content = JsonContent.Create(new
            {
                fullName,
                email = $"{Guid.NewGuid():N}@example.com",
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

    private static async Task<(List<Guid> Items, string? NextCursor)> GetPageAsync(
        HttpClient client, string token, int limit, string? cursor)
    {
        var url = cursor is null
            ? $"/resumes?limit={limit}"
            : $"/resumes?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url).WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadPageAsync(response);
    }

    private static async Task<(List<Guid> Items, string? NextCursor)> ReadPageAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = json.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetProperty("value").GetGuid())
            .ToList();

        var nextCursor = json.RootElement.GetProperty("nextCursor");
        return (items, nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString());
    }
}

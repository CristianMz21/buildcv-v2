using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Api.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// DELETE /v1/resumes/{id}/{section}/{itemId} over HTTP, end to end.
//
// It runs against the in-memory provider like the rest of this suite, which is the reason
// InMemoryResumeRepository identifies an entry by reference: without that these assertions would be
// about list positions and would say nothing about what SQL Server does.
public sealed class ResumeItemDeleteTests
{
    [Fact]
    public async Task Delete_RemovesTheEntryTheIdNames_NotTheFirstOneHoldingTheSameValue()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);

        // Byte-identical, and awards enforce no uniqueness — so the aggregate cannot tell these two
        // apart by value. This is the case the whole id scheme exists for, and the case the old
        // Remove(value) path got WRONG: it removes the first structurally-equal entry, so deleting the
        // second would have taken the first and left the id the caller named still present.
        await AddAwardAsync(client, token, resumeId, "Employee of the Year");
        await AddAwardAsync(client, token, resumeId, "Employee of the Year");

        var awards = await AwardIdsAsync(client, token, resumeId);
        awards.Should().HaveCount(2);
        awards[0].Should().NotBe(awards[1], "two entries of one CV must never share an id");

        var deleted = await DeleteAsync(client, token, resumeId, "awards", awards[1]);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var remaining = await AwardIdsAsync(client, token, resumeId);
        remaining.Should().Equal(
            [awards[0]],
            "the entry named by the id must be the one that goes, and the other must keep its id");
    }

    [Fact]
    public async Task Delete_WithAnIdThatNamesNoEntry_Answers404AndRemovesNothing()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);
        await AddAwardAsync(client, token, resumeId, "Employee of the Year");
        var awards = await AwardIdsAsync(client, token, resumeId);

        var response = await DeleteAsync(client, token, resumeId, "awards", awards[0] + 9999);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AwardIdsAsync(client, token, resumeId)).Should().Equal(awards[0]);
    }

    // An id is unique within ONE resume, so the same number routinely names an entry on another CV. A
    // client aiming its own valid id at somebody else's resume must learn nothing from the answer, and
    // must certainly not delete anything: the ownership check runs before the id is looked at.
    [Fact]
    public async Task Delete_AimedAtSomebodyElsesResume_IsRefusedAndLeavesTheirEntryAlone()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, ownerToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var victimResume = await CreateResumeAsync(client, ownerToken);
        await AddAwardAsync(client, ownerToken, victimResume, "Employee of the Year");
        var victimAwards = await AwardIdsAsync(client, ownerToken, victimResume);

        var (_, intruderToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail);

        var response = await DeleteAsync(client, intruderToken, victimResume, "awards", victimAwards[0]);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AwardIdsAsync(client, ownerToken, victimResume)).Should().Equal(victimAwards[0]);
    }

    // Ids are not recycled. A client that retries a delete it already made must be told the entry is
    // gone, not silently take out whatever moved into that slot.
    [Fact]
    public async Task Delete_RepeatedWithTheSameId_Answers404TheSecondTime()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);
        await AddAwardAsync(client, token, resumeId, "First");
        await AddAwardAsync(client, token, resumeId, "Second");
        var awards = await AwardIdsAsync(client, token, resumeId);

        (await DeleteAsync(client, token, resumeId, "awards", awards[0]))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DeleteAsync(client, token, resumeId, "awards", awards[0]))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await AwardIdsAsync(client, token, resumeId)).Should().Equal(awards[1]);
    }

    // Every collection is reachable, and each route reaches its OWN collection. A table of ten routes
    // is where a copy-paste sends `interests` at the publications list; this walks them all and checks
    // that deleting from one leaves the other nine untouched.
    [Fact]
    public async Task Delete_OnEachSection_TouchesOnlyThatSection()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);

        await AddAwardAsync(client, token, resumeId, "An award");
        await PostAsync(client, token, resumeId, "skills", new AddSkillRequest("C#", null, null));
        await PostAsync(client, token, resumeId, "interests", new AddInterestRequest("Chess", []));

        var before = await SectionIdsAsync(client, token, resumeId);
        before["awards"].Should().ContainSingle();
        before["skills"].Should().ContainSingle();
        before["interests"].Should().ContainSingle();

        (await DeleteAsync(client, token, resumeId, "skills", before["skills"][0]))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await SectionIdsAsync(client, token, resumeId);
        after["skills"].Should().BeEmpty();
        after["awards"].Should().Equal(before["awards"], "deleting a skill must not touch the awards");
        after["interests"].Should().Equal(before["interests"]);
    }

    private static async Task<Guid> CreateResumeAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes")
        {
            Content = JsonContent.Create(
                new CreateResumeRequest("Jane Candidate", "jane@example.com", null, null, null))
        };
        var response = await client.SendAsync(request.WithBearer(token));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static Task AddAwardAsync(HttpClient client, string token, Guid resumeId, string title) =>
        PostAsync(client, token, resumeId, "awards", new AddAwardRequest(title, null, null, null));

    private static async Task PostAsync<T>(
        HttpClient client, string token, Guid resumeId, string segment, T payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/resumes/{resumeId}/{segment}")
        {
            Content = JsonContent.Create(payload)
        };
        var response = await client.SendAsync(request.WithBearer(token));
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"adding a {segment} entry must succeed");
    }

    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, string token, Guid resumeId, string segment, int itemId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"/v1/resumes/{resumeId}/{segment}/{itemId}");
        return await client.SendAsync(request.WithBearer(token));
    }

    private static async Task<List<int>> AwardIdsAsync(HttpClient client, string token, Guid resumeId) =>
        (await SectionIdsAsync(client, token, resumeId))["awards"];

    private static async Task<Dictionary<string, List<int>>> SectionIdsAsync(
        HttpClient client, string token, Guid resumeId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/resumes/{resumeId}");
        var response = await client.SendAsync(request.WithBearer(token));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        string[] sections =
        [
            "experiences", "educations", "skills", "projects", "certificates",
            "languages", "awards", "publications", "interests", "references"
        ];

        return sections.ToDictionary(
            section => section,
            section => root.GetProperty(section).EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetInt32()).ToList());
    }
}

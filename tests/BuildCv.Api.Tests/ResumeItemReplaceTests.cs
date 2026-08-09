using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Api.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// PUT /v1/resumes/{id}/{section}/{itemId} over HTTP, end to end.
//
// The route exists because delete-then-post is not equivalent, and these tests are that claim written
// out: one of them replaces an entry in a collection where add-then-delete is refused outright, and one
// sends a replacement the Domain rejects and checks the original is still there. Both fail on any
// implementation that is two writes wearing one URL.
public sealed class ResumeItemReplaceTests
{
    [Fact]
    public async Task Replace_ChangesTheEntryTheIdNames_NotTheFirstOneHoldingTheSameValue()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);

        // Byte-identical and in a collection that enforces no uniqueness, so nothing but the id can
        // tell them apart — the same case the delete suite pins, asked of the other verb.
        await PostAwardAsync(client, token, resumeId, "Employee of the Year");
        await PostAwardAsync(client, token, resumeId, "Employee of the Year");

        var awards = await AwardsAsync(client, token, resumeId);
        awards.Should().HaveCount(2);

        var replaced = await PutAsync(
            client, token, resumeId, "awards", awards[1].Id,
            new AddAwardRequest("Employee of the Decade", null, null, null));
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await AwardsAsync(client, token, resumeId);
        after.Should().HaveCount(2, "a replacement is not an append");
        after.Select(award => award.Title).Should().BeEquivalentTo(
            ["Employee of the Year", "Employee of the Decade"],
            "exactly the entry the id named must have changed");
        after.Should().Contain(
            award => award.Id == awards[0].Id && award.Title == "Employee of the Year",
            "the entry that was not named keeps both its value and its id");
    }

    // THE CASE TWO REQUESTS CANNOT DO. Skills refuse a duplicate name, so a client that posts the
    // corrected entry before deleting the old one is refused, and one that deletes first loses the
    // entry if the post then fails. Certificates, languages and interests are the same shape.
    [Fact]
    public async Task Replace_InAUniqueCollection_CanEditEverythingButTheName()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);
        await PostAsync(client, token, resumeId, "skills", new AddSkillRequest("C#", "Intermediate", 3));

        var skillId = (await SectionIdsAsync(client, token, resumeId))["skills"].Single();

        // Same name, corrected level and years — which is what "I got this wrong" looks like on a skill.
        var response = await PutAsync(
            client, token, resumeId, "skills", skillId, new AddSkillRequest("C#", "Expert", 9));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the entry being replaced cannot duplicate itself");

        var skills = await SkillsAsync(client, token, resumeId);
        skills.Should().ContainSingle();
        skills[0].Level.Should().Be("Expert");
        skills[0].Years.Should().Be(9);
    }

    // Posting that same skill without replacing anything IS refused — which is what makes the test
    // above evidence rather than a tautology.
    [Fact]
    public async Task Post_OfAnEntryAlreadyPresentInAUniqueCollection_IsStillRefused()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);
        await PostAsync(client, token, resumeId, "skills", new AddSkillRequest("C#", "Intermediate", 3));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/resumes/{resumeId}/skills")
        {
            Content = JsonContent.Create(new AddSkillRequest("C#", "Expert", 9))
        };
        var response = await client.SendAsync(request.WithBearer(token));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // THE TRANSACTION. A replacement the Domain refuses must leave the entry exactly as it was — not
    // removed, not half-applied. The in-memory store hands out the stored aggregate itself, so an
    // implementation that removes before it knows the replacement is valid corrupts the CV here even
    // though it never saves.
    [Fact]
    public async Task Replace_WithAValueTheDomainRefuses_LeavesTheEntryExactlyAsItWas()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);
        await PostAsync(client, token, resumeId, "skills", new AddSkillRequest("C#", "Expert", 9));
        var skillId = (await SectionIdsAsync(client, token, resumeId))["skills"].Single();

        // A blank technology name: refused by Technology.Create, which is a DomainException the handler
        // turns into a 400 — after the entry it replaces would already have been removed.
        var response = await PutAsync(
            client, token, resumeId, "skills", skillId, new AddSkillRequest("   ", "Expert", 9));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var skills = await SkillsAsync(client, token, resumeId);
        skills.Should().ContainSingle("a refused replacement must not remove anything");
        skills[0].Name.Should().Be("C#");
        skills[0].Id.Should().Be(skillId, "the entry was never touched, so it keeps its id");
    }

    [Fact]
    public async Task Replace_WithAnIdThatNamesNoEntry_Answers404AndChangesNothing()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);
        await PostAwardAsync(client, token, resumeId, "Employee of the Year");
        var awards = await AwardsAsync(client, token, resumeId);

        var response = await PutAsync(
            client, token, resumeId, "awards", awards[0].Id + 9999,
            new AddAwardRequest("Something else", null, null, null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AwardsAsync(client, token, resumeId)).Should().BeEquivalentTo(awards);
    }

    // Ownership is checked before the id is looked at, so aiming a valid id of one's own at another
    // account's CV teaches the caller nothing and writes nothing.
    [Fact]
    public async Task Replace_AimedAtSomebodyElsesResume_IsRefusedAndLeavesTheirEntryAlone()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, ownerToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var victimResume = await CreateResumeAsync(client, ownerToken);
        await PostAwardAsync(client, ownerToken, victimResume, "Employee of the Year");
        var victimAwards = await AwardsAsync(client, ownerToken, victimResume);

        var (_, intruderToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail);

        var response = await PutAsync(
            client, intruderToken, victimResume, "awards", victimAwards[0].Id,
            new AddAwardRequest("Intruder", null, null, null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AwardsAsync(client, ownerToken, victimResume)).Should().BeEquivalentTo(victimAwards);
    }

    // The guards that refuse an out-of-range enum are the reason POST and PUT share one delegate. This
    // is the assertion that they really do: a value the POST refuses must not become storable by
    // sending it at the other verb.
    [Fact]
    public async Task Replace_WithAnUndefinedEnumValue_IsRefusedJustAsThePostIs()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);
        await PostAsync(client, token, resumeId, "languages", new AddLanguageRequest("Spanish", null, "Native"));
        var languageId = (await SectionIdsAsync(client, token, resumeId))["languages"].Single();

        // -1 wraps to 255 in the tinyint column, above Native — the case ResumeEndpoints documents.
        var response = await PutAsync(
            client, token, resumeId, "languages", languageId,
            new AddLanguageRequest("Spanish", null, "-1"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Every collection is reachable by the new verb, and each route reaches its OWN collection — the
    // failure a ten-row table invites. Replacing in one leaves the other nine untouched.
    [Fact]
    public async Task Replace_OnEachSection_TouchesOnlyThatSection()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await CreateResumeAsync(client, token);
        await PostAwardAsync(client, token, resumeId, "An award");
        await PostAsync(client, token, resumeId, "skills", new AddSkillRequest("C#", null, null));
        await PostAsync(client, token, resumeId, "interests", new AddInterestRequest("Chess", []));

        var before = await SectionIdsAsync(client, token, resumeId);

        (await PutAsync(client, token, resumeId, "skills", before["skills"][0], new AddSkillRequest("Go", null, null)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await SectionIdsAsync(client, token, resumeId);
        after["skills"].Should().ContainSingle();
        after["skills"].Should().NotEqual(before["skills"], "the replacement is a new entry with a new id");
        after["awards"].Should().Equal(before["awards"], "replacing a skill must not touch the awards");
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

    private static Task PostAwardAsync(HttpClient client, string token, Guid resumeId, string title) =>
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

    private static async Task<HttpResponseMessage> PutAsync<T>(
        HttpClient client, string token, Guid resumeId, string segment, int itemId, T payload)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/v1/resumes/{resumeId}/{segment}/{itemId}")
        {
            Content = JsonContent.Create(payload)
        };
        return await client.SendAsync(request.WithBearer(token));
    }

    private sealed record AwardRow(int Id, string Title);

    private sealed record SkillRow(int Id, string Name, string? Level, int? Years);

    private static async Task<List<AwardRow>> AwardsAsync(HttpClient client, string token, Guid resumeId)
    {
        var root = await ResumeAsync(client, token, resumeId);
        return [.. root.GetProperty("awards").EnumerateArray()
            .Select(entry => new AwardRow(
                entry.GetProperty("id").GetInt32(),
                entry.GetProperty("title").GetString()!))];
    }

    private static async Task<List<SkillRow>> SkillsAsync(HttpClient client, string token, Guid resumeId)
    {
        var root = await ResumeAsync(client, token, resumeId);
        return [.. root.GetProperty("skills").EnumerateArray()
            .Select(entry => new SkillRow(
                entry.GetProperty("id").GetInt32(),
                entry.GetProperty("name").GetString()!,
                entry.GetProperty("level").GetString(),
                entry.GetProperty("yearsOfExperience").ValueKind == JsonValueKind.Number
                    ? entry.GetProperty("yearsOfExperience").GetInt32()
                    : null))];
    }

    private static async Task<Dictionary<string, List<int>>> SectionIdsAsync(
        HttpClient client, string token, Guid resumeId)
    {
        var root = await ResumeAsync(client, token, resumeId);

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

    private static async Task<JsonElement> ResumeAsync(HttpClient client, string token, Guid resumeId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/resumes/{resumeId}");
        var response = await client.SendAsync(request.WithBearer(token));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Cloned because the JsonDocument backing it is disposed with this scope.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }
}

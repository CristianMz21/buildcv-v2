using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Api.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// The candidate profile surface, end to end: GET /v1/profile, PUT /v1/profile/contact, and the ten
// POST/PUT/DELETE item routes. Runs on the in-memory provider like the rest of the suite.
public sealed class CandidateProfileContractTests
{
    [Fact]
    public async Task Get_without_a_profile_answers404()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/profile");
        var response = await client.SendAsync(request.WithBearer(token));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Contact_put_creates_the_profile_so_a_later_get_reads_it()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var created = await PutContactAsync(client, token,
            new UpdateContactRequest("Jane Doe", "jane@example.com", null, "Buenos Aires", null));
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await GetProfileAsync(client, token);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("contactInformation").GetProperty("fullName").GetString()
            .Should().Be("Jane Doe");
    }

    [Fact]
    public async Task Item_routes_round_trip_append_get_replace_and_delete()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        await PutContactAsync(client, token,
            new UpdateContactRequest("Jane Doe", "jane@example.com", null, null, null));

        await PostAsync(client, token, "awards", new AddAwardRequest("Employee of the Year", null, null, null));
        await PostAsync(client, token, "awards", new AddAwardRequest("Second", null, null, null));

        var awards = await SectionIdsAsync(client, token, "awards");
        awards.Should().HaveCount(2);
        awards[0].Should().NotBe(awards[1], "two entries of one profile must never share an id");

        var replaced = await PutAsync(client, token, "awards", awards[1],
            new AddAwardRequest("Second (updated)", null, null, null));
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterReplace = await SectionIdsAsync(client, token, "awards");
        afterReplace.Should().HaveCount(2);
        afterReplace[1].Should().NotBe(awards[1], "a replace is a NEW entry with a NEW id");

        var deleted = await DeleteAsync(client, token, "awards", awards[0]);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SectionIdsAsync(client, token, "awards")).Should().Equal(afterReplace[1]);
    }

    // The profile is looked up BY the requester's owner id, so another account finds no profile at all
    // and learns nothing — not even whether one exists. "not found" here is the property, not a gap.
    [Fact]
    public async Task Foreign_account_never_reads_or_writes_anothers_profile()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, ownerToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        await PutContactAsync(client, ownerToken,
            new UpdateContactRequest("Jane Doe", "jane@example.com", null, null, null));

        var (_, intruderToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail);

        using var get = new HttpRequestMessage(HttpMethod.Get, "/v1/profile");
        (await client.SendAsync(get.WithBearer(intruderToken))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var post = await PostAsync(client, intruderToken, "awards",
            new AddAwardRequest("Mine", null, null, null));
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var getAfter = await GetProfileAsync(client, ownerToken);
        using var body = JsonDocument.Parse(await getAfter.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("awards").EnumerateArray().Should().BeEmpty(
            "the intruder's refused write must not have created anything on the owner's profile");
    }

    [Fact]
    public async Task Item_routes_without_a_profile_answer404()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostAsync(client, token, "skills", new AddSkillRequest("C#", null, null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Item_routes_without_authentication_answer401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsJsonAsync("/v1/profile/skills", new AddSkillRequest("C#", null, null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // TryParse accepts any numeric string and the level is mapped to tinyint with an unchecked
    // conversion — an undefined LanguageProficiency would wrap to 255 in the column and read as above
    // Native. The endpoint guard must refuse it before the handler runs.
    [Fact]
    public async Task Out_of_range_language_level_is_a400_not_a_stored_byte()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        await PutContactAsync(client, token,
            new UpdateContactRequest("Jane Doe", "jane@example.com", null, null, null));

        var response = await PostAsync(client, token, "languages",
            new AddLanguageRequest("Spanish", "Native", "300"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<HttpResponseMessage> PutContactAsync(
        HttpClient client, string token, UpdateContactRequest contact)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/v1/profile/contact")
        {
            Content = JsonContent.Create(contact)
        };
        return await client.SendAsync(request.WithBearer(token));
    }

    private static async Task<HttpResponseMessage> GetProfileAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/profile");
        return await client.SendAsync(request.WithBearer(token));
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(
        HttpClient client, string token, string segment, T payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/profile/{segment}")
        {
            Content = JsonContent.Create(payload)
        };
        return await client.SendAsync(request.WithBearer(token));
    }

    private static async Task<HttpResponseMessage> PutAsync<T>(
        HttpClient client, string token, string segment, int itemId, T payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1/profile/{segment}/{itemId}")
        {
            Content = JsonContent.Create(payload)
        };
        return await client.SendAsync(request.WithBearer(token));
    }

    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, string token, string segment, int itemId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/profile/{segment}/{itemId}");
        return await client.SendAsync(request.WithBearer(token));
    }

    private static async Task<List<int>> SectionIdsAsync(
        HttpClient client, string token, string section)
    {
        var response = await GetProfileAsync(client, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty(section).EnumerateArray()
            .Select(entry => entry.GetProperty("id").GetInt32()).ToList();
    }
}

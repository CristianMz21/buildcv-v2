using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// The write path for the two dimensions PR 3's engine will read. A validated, persisted field that no
// client can fill in is exactly the JobRequirement.Weight mistake this phase is removing, so the
// fields are exercised over HTTP rather than trusted because the Domain compiles.
//
// The level arrives as a STRING and is parsed in the endpoint, matching how AddSkillRequest.Level is
// already handled: a name the enum does not know is a 400 before the handler runs, not a silent null
// that would leave the candidate wondering why their advice never changed.
public sealed class ResumeLevelFieldsTests
{
    [Fact]
    public async Task AddLanguage_WithAProficiencyLevel_StoresTheLevelBesideTheFreeTextFluency()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/resumes/{resumeId}/languages", new
        {
            name = "Español",
            fluency = "Bilingüe",
            level = "Native"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var language = (await GetResumeAsync(client, token, resumeId))
            .GetProperty("languages").EnumerateArray().Single();

        // Asymmetric, and pre-existing: the level goes IN as a name and comes back OUT as a number,
        // because no JsonStringEnumConverter is configured and Skill.Level and Experience.Type already
        // behave this way. The literal is LanguageProficiency.Native's pinned value, so this doubles as
        // a wire-level guard on the numbering the tinyint column depends on.
        language.GetProperty("level").GetInt32().Should().Be(4);

        // The whole point of the pair. "Bilingüe" is in no normalization table anyone would write, and
        // parsing it would score a native Spanish speaker zero on Spanish. It is kept verbatim for
        // display and the level is stated separately by the candidate.
        language.GetProperty("fluency").GetString().Should().Be("Bilingüe");
    }

    // ignoreCase: true, so the casing a client happens to send is not a trap.
    [Theory]
    [InlineData("native")]
    [InlineData("NATIVE")]
    [InlineData("Native")]
    public async Task AddLanguage_AcceptsTheLevelNameInAnyCasing(string level)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/resumes/{resumeId}/languages", new
        {
            name = "English",
            fluency = (string?)null,
            level
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AddLanguage_WithNoLevel_SucceedsAndStoresNone()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/resumes/{resumeId}/languages", new
        {
            name = "English",
            fluency = "Conversational-ish"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Missing DATA, not a low level. PR 3 turns this into a recommendation naming the fix; a
        // default of Basic here would instead penalise the candidate for not filling in a field.
        var language = (await GetResumeAsync(client, token, resumeId))
            .GetProperty("languages").EnumerateArray().Single();
        language.GetProperty("level").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("Bilingue")]
    [InlineData("C2")]
    [InlineData("")]
    public async Task AddLanguage_WithALevelTheEnumDoesNotKnow_IsABadRequest(string level)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/resumes/{resumeId}/languages", new
        {
            name = "Español",
            fluency = "Nativo",
            level
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        // Rejected BEFORE the handler ran, so nothing was written.
        (await GetResumeAsync(client, token, resumeId))
            .GetProperty("languages").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AddEducation_WithALevel_StoresItBesideTheFreeTextDegree()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/resumes/{resumeId}/educations", new
        {
            institution = "Universidad de Buenos Aires",
            degree = "Ingeniero en Sistemas",
            fieldOfStudy = "Sistemas",
            start = "2015-03-01",
            end = "2020-12-01",
            grade = (string?)null,
            level = "Bachelor"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var education = (await GetResumeAsync(client, token, resumeId))
            .GetProperty("educations").EnumerateArray().Single();

        // EducationLevel.Bachelor's pinned number. See the note on the language test above for why the
        // response is a number while the request was a name.
        education.GetProperty("level").GetInt32().Should().Be(2);
        education.GetProperty("degree").GetString().Should().Be("Ingeniero en Sistemas",
            "the degree is free text and is never parsed into the level");
    }

    [Theory]
    [InlineData("Licenciatura")]
    [InlineData("PhD")]
    public async Task AddEducation_WithALevelTheEnumDoesNotKnow_IsABadRequest(string level)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/resumes/{resumeId}/educations", new
        {
            institution = "Universidad de Buenos Aires",
            degree = "Ingeniero en Sistemas",
            fieldOfStudy = (string?)null,
            start = "2015-03-01",
            end = (string?)null,
            grade = (string?)null,
            level
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        (await GetResumeAsync(client, token, resumeId))
            .GetProperty("educations").GetArrayLength().Should().Be(0);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string token, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        }.WithBearer(token);

        return await client.SendAsync(request);
    }

    private static async Task<Guid> CreateResumeAsync(HttpClient client, string token)
    {
        var response = await PostAsync(client, token, "/resumes", new
        {
            fullName = "Jane Candidate",
            email = $"{Guid.NewGuid():N}@example.com",
            phoneNumber = (string?)null,
            location = (string?)null,
            summary = (string?)null,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetProperty("value").GetGuid();
    }

    private static async Task<JsonElement> GetResumeAsync(HttpClient client, string token, Guid resumeId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/resumes/{resumeId}").WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Cloned: the JsonDocument owns the buffer the element points into and is disposed on return.
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}

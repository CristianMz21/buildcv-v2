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

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/languages", new
        {
            name = "Español",
            fluency = "Bilingüe",
            level = "Native"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var language = (await GetResumeAsync(client, token, resumeId))
            .GetProperty("languages").EnumerateArray().Single();

        // Symmetric since v1: the level goes IN as a name and comes back OUT as the same name. It used
        // to come back as the tinyint 4, because the endpoint answered with the Domain aggregate and no
        // JsonStringEnumConverter is configured; ResumeResponse states the encoding instead, so the
        // persisted numbering is no longer part of this API's contract.
        language.GetProperty("level").GetString().Should().Be("Native");

        // The whole point of the pair. "Bilingüe" is in no normalization table anyone would write, and
        // parsing it would score a native Spanish speaker zero on Spanish. It is kept verbatim for
        // display and the level is stated separately by the candidate.
        language.GetProperty("fluency").GetString().Should().Be("Bilingüe");
    }

    // ignoreCase: true, so the casing a client happens to send is not a trap. The STORED value is
    // asserted, not just the status: an endpoint that parsed the level and then dropped it on the
    // floor would answer 200 all day.
    [Theory]
    [InlineData("native")]
    [InlineData("NATIVE")]
    [InlineData("Native")]
    [InlineData("NaTiVe")]
    public async Task AddLanguage_AcceptsTheLevelNameInAnyCasing(string level)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/languages", new
        {
            name = "English",
            fluency = (string?)null,
            level
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetResumeAsync(client, token, resumeId))
            .GetProperty("languages").EnumerateArray().Single()
            .GetProperty("level").GetString().Should().Be("Native");
    }

    // A DEFINED number is still accepted, and the IsDefined guard must not start rejecting one. The
    // reason has changed with v1 and is worth stating precisely: GET used to answer the level as a
    // NUMBER, so numeric input was what read-modify-write required. GET answers the NAME now, so a
    // round-tripping client sends "Native" and never needs this — what the tolerance protects is every
    // caller written against the old shape, and the guard below still has to tell 0 and 4 (real
    // members) from 99, 300 and -1 (not members, and silently corrupting before the guard existed).
    [Theory]
    [InlineData("0", "Basic")]
    [InlineData("4", "Native")]
    public async Task AddLanguage_AcceptsAValidNumericLevel_SoAnOldClientKeepsWorking(
        string level, string expected)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/languages", new
        {
            name = "English",
            fluency = (string?)null,
            level
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetResumeAsync(client, token, resumeId))
            .GetProperty("languages").EnumerateArray().Single()
            .GetProperty("level").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task AddLanguage_WithNoLevel_SucceedsAndStoresNone()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/languages", new
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

    // The numeric cases are the ones that matter, and none of them is a name Enum.TryParse rejects —
    // TryParse accepts ANY numeric string. Measured against SQL Server before the IsDefined guard
    // existed: 99 stored as 99, 300 truncated to 44, and -1 wrapped to 255, all silently, because the
    // tinyint conversion is unchecked. 255 is above Native, so "-1" — the most obviously-invalid input
    // a fuzzer sends — became MAXIMUM proficiency, and PR 3 would tell that candidate they meet a
    // requirement they do not. Removing IsDefined from the endpoint fails exactly these three.
    [Theory]
    [InlineData("Bilingue")]
    [InlineData("C2")]
    [InlineData("")]
    [InlineData("99")]
    [InlineData("300")]
    [InlineData("-1")]
    public async Task AddLanguage_WithALevelTheEnumDoesNotKnow_IsABadRequest(string level)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/languages", new
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

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/educations", new
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

        // The name in, the same name out — see the language test above for the v1 encoding this
        // replaced, where the request carried "Bachelor" and the response answered 2.
        education.GetProperty("level").GetString().Should().Be("Bachelor");
        education.GetProperty("degree").GetString().Should().Be("Ingeniero en Sistemas",
            "the degree is free text and is never parsed into the level");
    }

    [Theory]
    [InlineData("Licenciatura")]
    [InlineData("PhD")]
    [InlineData("99")]
    [InlineData("300")]
    [InlineData("-1")]
    public async Task AddEducation_WithALevelTheEnumDoesNotKnow_IsABadRequest(string level)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/educations", new
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

    // The two sites issue #21 named. They are in this file rather than beside the endpoints' own tests
    // because the rule is the same one the language and education blocks above state, and the guard is
    // now symmetric across all four: an undefined value never reaches a tinyint column.
    //
    // BEHAVIOUR CHANGE. Every numeric row below answered 200 before this guard and answered it by
    // storing a mangled byte — measured on the CLR and reproduced against the tinyint conversion: 99
    // stays 99, 300 truncates to 44, -1 wraps to 255. Nothing scores Skill.Level today, so what was at
    // stake was durable data no reader can interpret rather than a wrong score. `"Experto"` and `""`
    // are the rows TryParse already refused; they are here so a regression that deleted the whole
    // parse block, not just IsDefined, is still visible.
    [Theory]
    [InlineData("Experto")]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("99")]
    [InlineData("300")]
    [InlineData("-1")]
    public async Task AddSkill_WithALevelTheEnumDoesNotKnow_IsABadRequest(string level)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/skills", new
        {
            skillName = "C#",
            level,
            yearsOfExperience = (int?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        // Refused BEFORE the handler ran, so nothing was written. Without this the test would pass for
        // an endpoint that answered 400 and stored the skill anyway.
        (await GetResumeAsync(client, token, resumeId))
            .GetProperty("skills").GetArrayLength().Should().Be(0);
    }

    // A DEFINED number is still accepted, and the guard must not start rejecting one. GET answers the
    // level as a NAME since v1 (SkillResponse.From calls ToString), so a round-tripping client sends
    // "Expert" and never needs this — what the tolerance protects is callers written against the
    // pre-v1 shape, which answered the tinyint. The pair is what makes the guard's job precise: tell
    // 0 and 3 (real members) from 4, 99, 300 and -1 (not members).
    [Theory]
    [InlineData("0", "Beginner")]
    [InlineData("1", "Intermediate")]
    [InlineData("3", "Expert")]
    public async Task AddSkill_AcceptsAValidNumericLevel_SoAnOldClientKeepsWorking(
        string level, string expected)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/skills", new
        {
            skillName = "C#",
            level,
            yearsOfExperience = (int?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetResumeAsync(client, token, resumeId))
            .GetProperty("skills").EnumerateArray().Single()
            .GetProperty("level").GetString().Should().Be(expected);
    }

    // ExperienceType has exactly two members, so every number from 2 up is undefined — 2 is here
    // rather than only the dramatic ones because "just past the end" is the value a client actually
    // sends after someone appends a third member to the enum in a later version.
    [Theory]
    [InlineData("Freelance")]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("99")]
    [InlineData("300")]
    [InlineData("-1")]
    public async Task AddExperience_WithATypeTheEnumDoesNotKnow_IsABadRequest(string type)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/experiences", new
        {
            type,
            organization = "Acme",
            position = "Backend Developer",
            start = "2020-01-01",
            end = (string?)null,
            summary = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        (await GetResumeAsync(client, token, resumeId))
            .GetProperty("experiences").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("0", "Professional")]
    [InlineData("1", "Volunteer")]
    [InlineData("Volunteer", "Volunteer")]
    public async Task AddExperience_AcceptsEveryDefinedType(string type, string expected)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await CreateResumeAsync(client, token);

        var response = await PostAsync(client, token, $"/v1/resumes/{resumeId}/experiences", new
        {
            type,
            organization = "Acme",
            position = "Backend Developer",
            start = "2020-01-01",
            end = (string?)null,
            summary = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetResumeAsync(client, token, resumeId))
            .GetProperty("experiences").EnumerateArray().Single()
            .GetProperty("type").GetString().Should().Be(expected);
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
        var response = await PostAsync(client, token, "/v1/resumes", new
        {
            fullName = "Jane Candidate",
            email = $"{Guid.NewGuid():N}@example.com",
            phoneNumber = (string?)null,
            location = (string?)null,
            summary = (string?)null,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetResumeAsync(HttpClient client, string token, Guid resumeId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/resumes/{resumeId}").WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Cloned: the JsonDocument owns the buffer the element points into and is disposed on return.
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}

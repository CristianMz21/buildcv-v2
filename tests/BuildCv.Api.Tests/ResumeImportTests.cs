using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BuildCv.Api.Endpoints;
using BuildCv.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// POST /resumes/import over HTTP. What is pinned here is the WIRE contract a review screen depends on,
// which the Application tests cannot see: that a whole CV survives the request/response round trip
// field for field, and that a rejected draft answers the standard ProblemDetails validation shape with
// an `errors` object keyed by JSON field path.
//
// The round-trip test sends a DISTINCT value in every field on purpose. ImportResumeRequest mirrors
// ResumeDraft by hand — CLAUDE.md forbids an Application type on the wire — so a swapped pair in that
// mapping is a real bug, and plausible-looking placeholder values would hide one.
public sealed class ResumeImportTests
{
    [Fact]
    public async Task Import_AFullDraft_CreatesTheWholeResumeInOneRequest()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostImportAsync(client, token, FullDraft());

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var resumeId = created.RootElement.GetProperty("id").GetProperty("value").GetGuid();
        response.Headers.Location!.ToString().Should().Be($"/v1/resumes/{resumeId}");

        var resume = await GetResumeAsync(client, token, resumeId);

        var contact = resume.GetProperty("contactInformation");
        contact.GetProperty("fullName").GetProperty("value").GetString().Should().Be("Jane Candidate");
        contact.GetProperty("email").GetProperty("value").GetString().Should().Be("jane@example.com");
        contact.GetProperty("phoneNumber").GetProperty("value").GetString().Should().Be("+541155550123");
        contact.GetProperty("location").GetString().Should().Be("Buenos Aires");
        contact.GetProperty("summary").GetString().Should().Be("Backend engineer.");

        // New capability, not a refactor: neither field could be set through any route before this one.
        contact.GetProperty("website").GetProperty("value").GetString().Should().Be("https://jane.example.com");
        var profile = contact.GetProperty("profiles").EnumerateArray().Single();
        profile.GetProperty("network").GetString().Should().Be("GitHub");
        profile.GetProperty("username").GetString().Should().Be("janedev");
        profile.GetProperty("url").GetProperty("value").GetString().Should().Be("https://github.com/janedev");

        var experience = resume.GetProperty("experiences").EnumerateArray().Single();
        experience.GetProperty("type").GetInt32().Should().Be(0, "ExperienceType.Professional");
        experience.GetProperty("organization").GetProperty("value").GetString().Should().Be("Mercado Libre");
        experience.GetProperty("position").GetString().Should().Be("Senior Engineer");
        experience.GetProperty("period").GetProperty("start").GetString().Should().Be("2019-03-01");
        experience.GetProperty("period").GetProperty("end").GetString().Should().Be("2023-06-30");
        experience.GetProperty("summary").GetString().Should().Be("Payments platform.");
        experience.GetProperty("highlights").EnumerateArray().Single().GetString()
            .Should().Be("Cut latency in half");

        var education = resume.GetProperty("educations").EnumerateArray().Single();
        education.GetProperty("institution").GetProperty("value").GetString()
            .Should().Be("Universidad de Buenos Aires");
        education.GetProperty("degree").GetString().Should().Be("Ingeniero en Sistemas");
        education.GetProperty("fieldOfStudy").GetString().Should().Be("Software");
        education.GetProperty("grade").GetString().Should().Be("8.4");
        education.GetProperty("level").GetInt32().Should().Be(2, "EducationLevel.Bachelor");

        var skill = resume.GetProperty("skills").EnumerateArray().Single();
        skill.GetProperty("name").GetProperty("name").GetString().Should().Be("C#");
        skill.GetProperty("level").GetInt32().Should().Be(2, "SkillLevel.Advanced");
        skill.GetProperty("yearsOfExperience").GetInt32().Should().Be(7);

        var project = resume.GetProperty("projects").EnumerateArray().Single();
        project.GetProperty("name").GetString().Should().Be("buildcv");
        project.GetProperty("description").GetString().Should().Be("A CV scorer.");
        project.GetProperty("repositoryUrl").GetProperty("value").GetString()
            .Should().Be("https://github.com/janedev/buildcv");
        project.GetProperty("liveDemoUrl").GetProperty("value").GetString()
            .Should().Be("https://buildcv.example.com");
        project.GetProperty("technologies").EnumerateArray().Single()
            .GetProperty("name").GetString().Should().Be("dotnet");
        project.GetProperty("highlights").EnumerateArray().Single().GetString()
            .Should().Be("Deterministic scoring");

        var certificate = resume.GetProperty("certificates").EnumerateArray().Single();
        certificate.GetProperty("name").GetString().Should().Be("AWS Solutions Architect");
        certificate.GetProperty("issuer").GetProperty("value").GetString().Should().Be("Amazon");
        certificate.GetProperty("credentialId").GetString().Should().Be("cred-123");
        certificate.GetProperty("credentialUrl").GetProperty("value").GetString()
            .Should().Be("https://aws.example.com/cred-123");
        certificate.GetProperty("validityPeriod").GetProperty("start").GetString().Should().Be("2024-01-01");
        certificate.GetProperty("validityPeriod").GetProperty("end").GetString().Should().Be("2027-01-01");

        var language = resume.GetProperty("languages").EnumerateArray().Single();
        language.GetProperty("name").GetString().Should().Be("Español");
        language.GetProperty("fluency").GetString().Should().Be("Bilingüe");
        language.GetProperty("level").GetInt32().Should().Be(4, "LanguageProficiency.Native");

        var award = resume.GetProperty("awards").EnumerateArray().Single();
        award.GetProperty("title").GetString().Should().Be("Best Hack");
        award.GetProperty("awarder").GetProperty("value").GetString().Should().Be("Hackathon AR");
        award.GetProperty("date").GetString().Should().Be("2021-11-05");
        award.GetProperty("summary").GetString().Should().Be("First place.");

        var publication = resume.GetProperty("publications").EnumerateArray().Single();
        publication.GetProperty("title").GetString().Should().Be("On Scoring");
        publication.GetProperty("publisher").GetProperty("value").GetString().Should().Be("ACM");
        publication.GetProperty("url").GetProperty("value").GetString()
            .Should().Be("https://acm.example.com/on-scoring");
        publication.GetProperty("releaseDate").GetString().Should().Be("2022-05-01");
        publication.GetProperty("summary").GetString().Should().Be("A paper.");

        var interest = resume.GetProperty("interests").EnumerateArray().Single();
        interest.GetProperty("name").GetString().Should().Be("Climbing");
        interest.GetProperty("keywords").EnumerateArray().Single().GetString().Should().Be("bouldering");

        var reference = resume.GetProperty("references").EnumerateArray().Single();
        reference.GetProperty("name").GetString().Should().Be("John Manager");
        reference.GetProperty("position").GetString().Should().Be("Engineering Manager");
        reference.GetProperty("company").GetProperty("value").GetString().Should().Be("Mercado Libre");
        reference.GetProperty("email").GetProperty("value").GetString().Should().Be("john@example.com");
        reference.GetProperty("phoneNumber").GetProperty("value").GetString().Should().Be("+541155550999");
        reference.GetProperty("referenceText").GetString().Should().Be("Would hire again.");
    }

    // The shape ASP.NET's own model validation emits, on purpose: a client that already understands
    // validation ProblemDetails needs no BuildCv-specific convention to highlight the offending inputs.
    [Fact]
    public async Task Import_WithBadFields_IsAProblemDetailsWithAnErrorsObjectKeyedByFieldPath()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostImportAsync(client, token, new
        {
            contact = new
            {
                fullName = "Jane Candidate",
                email = "jane@example.com",
                phoneNumber = "(555) 123-4567"
            },
            experiences = new[]
            {
                new
                {
                    type = "Professional",
                    organization = "Globant",
                    position = "Engineer",
                    start = "2020-01-01",
                    end = "2019-01-01"
                }
            },
            languages = new[] { new { name = "Español", level = "Avanzado" } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);

        var errors = problem.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(field => field.Name).Should().BeEquivalentTo(
            ["contact.phoneNumber", "experiences[0].end", "languages[0].level"]);

        errors.GetProperty("languages[0].level").EnumerateArray().Single().GetString()
            .Should().Be("Invalid language proficiency.");
        errors.GetProperty("experiences[0].end").EnumerateArray().Single().GetString()
            .Should().Be("End date must be null or on/after start date.");
    }

    [Fact]
    public async Task Import_WithBadFields_CreatesNoResume()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostImportAsync(client, token, new
        {
            contact = new { fullName = "Jane Candidate", email = "jane@example.com" },
            skills = new[] { new { name = "C#" }, new { name = "Go" } },
            languages = new[] { new { name = "Español", level = "Avanzado" } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The list, not the response: "it answered 400" and "nothing was stored" are different claims,
        // and a half-import would satisfy only the first.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/resumes").WithBearer(token);
        var list = await client.SendAsync(request);
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // Over real HTTP, because this is where it was a 500: `[null]` binds to a one-element list holding
    // null — System.Text.Json ignores nullable reference annotations — and the mapping in
    // ImportResumeRequest.ToDraft then dereferenced it. The plain string arrays never had the hole, which
    // is what made the object arrays look deliberate.
    [Theory]
    [InlineData("experiences")]
    [InlineData("educations")]
    [InlineData("skills")]
    [InlineData("projects")]
    [InlineData("certificates")]
    [InlineData("languages")]
    [InlineData("awards")]
    [InlineData("publications")]
    [InlineData("interests")]
    [InlineData("references")]
    public async Task Import_WithANullArrayElement_IsAFieldErrorNotAServerError(string section)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var body = JsonSerializer.Deserialize<JsonElement>($$"""
            {"contact":{"fullName":"Jane Candidate","email":"jane@example.com"},"{{section}}":[null]}
            """);

        var response = await PostImportAsync(client, token, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name)
            .Should().BeEquivalentTo([$"{section}[0]"]);
    }

    [Fact]
    public async Task Import_WithANullProfileElement_IsAFieldErrorNotAServerError()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var body = JsonSerializer.Deserialize<JsonElement>("""
            {"contact":{"fullName":"Jane Candidate","email":"jane@example.com","profiles":[null]}}
            """);

        var response = await PostImportAsync(client, token, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name)
            .Should().BeEquivalentTo(["contact.profiles[0]"]);
    }

    // Website and Profiles were unreachable before this endpoint, which is exactly why nothing noticed
    // that PUT /resumes/{id}/contact rebuilds the contact through ContactInformationFactory — a factory
    // that hardcodes a null Website and an empty Profiles list. The moment import can fill them, that
    // route answers 200 and silently erases a candidate's site and every social handle because they
    // corrected their city.
    [Fact]
    public async Task UpdateContact_AfterAnImport_KeepsTheWebsiteAndProfiles()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var created = await PostImportAsync(client, token, FullDraft());
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var resumeId = body.RootElement.GetProperty("id").GetProperty("value").GetGuid();

        using var update = new HttpRequestMessage(HttpMethod.Put, $"/v1/resumes/{resumeId}/contact")
        {
            Content = JsonContent.Create(new
            {
                fullName = "Jane Candidate",
                email = "jane@example.com",
                phoneNumber = "+541155550123",
                location = "Córdoba",
                summary = "Backend engineer.",
            }),
        }.WithBearer(token);

        (await client.SendAsync(update)).StatusCode.Should().Be(HttpStatusCode.OK);

        var contact = (await GetResumeAsync(client, token, resumeId)).GetProperty("contactInformation");

        contact.GetProperty("location").GetString().Should().Be("Córdoba", "the update did apply");
        contact.GetProperty("website").GetProperty("value").GetString()
            .Should().Be("https://jane.example.com", "an unsent field means unchanged, not deleted");
        contact.GetProperty("profiles").EnumerateArray().Single()
            .GetProperty("network").GetString().Should().Be("GitHub");
    }

    // Certificate.Name and Interest.Name are classified CONFIDENTIAL and encrypted at rest. The duplicate
    // messages used to quote them back — "Certificate '<value>' already exists." — putting the candidate's
    // own data in plaintext inside a string a review screen renders. The path already carries the index,
    // so the value bought nothing.
    [Fact]
    public async Task Import_WithADuplicate_DoesNotEchoTheValueIntoTheErrorBody()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        const string Marker = "SECRET-HOBBY-<img src=x onerror=alert(1)>";

        var response = await PostImportAsync(client, token, new
        {
            contact = new { fullName = "Jane Candidate", email = "jane@example.com" },
            interests = new[] { new { name = Marker }, new { name = Marker } },
            certificates = new[]
            {
                new { name = Marker, issuer = "CNCF" },
                new { name = Marker, issuer = "CNCF" }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("SECRET-HOBBY", "the candidate's own value must not travel back in an error string");
        body.Should().NotContain("onerror");

        using var problem = JsonDocument.Parse(body);
        var errors = problem.RootElement.GetProperty("errors");
        errors.GetProperty("interests[1].name").EnumerateArray().Single().GetString()
            .Should().Be("Duplicates the interest at index 0.");
        errors.GetProperty("certificates[1].name").EnumerateArray().Single().GetString()
            .Should().Be("Duplicates the certificate at index 0.");
    }

    // The CSRF guard covers this route because it covers every cookie-authenticated unsafe method, but
    // nothing pinned it here, and a route added to CsrfGuardMiddleware.ExemptPaths by mistake would fail
    // no test. Both directions, so the 201 proves the 403 is about the token and not about the request.
    [Fact]
    public async Task Import_FromACookieClientWithoutTheCsrfToken_IsForbidden()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = JsonContent.Create(FullDraft()),
        };

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Import_FromACookieClientWithTheCsrfToken_IsCreated()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var csrfToken = await client.GetAntiforgeryTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = JsonContent.Create(FullDraft()),
        };
        request.Headers.Add(CsrfGuardMiddleware.CsrfHeaderName, csrfToken);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // Per ACCOUNT, not per IP. The global 100/min per-IP limiter was the only ceiling on the most durable
    // write in this API — measured, 95 imports went through before the first 429.
    [Fact]
    public async Task Import_BeyondItsPerAccountCeiling_IsThrottled()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        for (var attempt = 0; attempt < ResumeImportRateLimiter.PermitLimit; attempt++)
        {
            (await PostImportAsync(client, token, FullDraft())).StatusCode
                .Should().Be(HttpStatusCode.Created, "attempt {0} is inside the window", attempt);
        }

        var throttled = await PostImportAsync(client, token, FullDraft());

        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter.Should().NotBeNull("a throttled client is told when to come back");
    }

    // A second account is unaffected, which is the whole point of keying on the account rather than the
    // address: the per-IP window would have throttled this caller too.
    [Fact]
    public async Task Import_ByAnotherAccountFromTheSameAddress_IsNotThrottled()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, first) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        for (var attempt = 0; attempt < ResumeImportRateLimiter.PermitLimit; attempt++)
            await PostImportAsync(client, first, FullDraft());

        (await PostImportAsync(client, first, FullDraft())).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests);

        var (_, second) = await client.RegisterAndLoginAsync("second-candidate@example.com");

        (await PostImportAsync(client, second, FullDraft())).StatusCode
            .Should().Be(HttpStatusCode.Created);
    }

    // Malformed bodies used to answer an EMPTY 400 in production and a logged 500 in Development. They are
    // the class of refusal that CAN be shaped from inside the app, unlike the 413 — see
    // MalformedRequestExceptionHandler.
    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    public async Task Import_WithAMalformedBody_IsProblemDetailsShaped(string body)
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = new StringContent(body, Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json),
        }.WithBearer(token);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a body the binder cannot read is the caller's fault, not a 500");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);
    }

    [Fact]
    public async Task Import_WithoutAuthentication_IsUnauthorized()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsJsonAsync("/v1/resumes/import", FullDraft());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Every leaf is a string on the wire — dates, the years count and the levels included — so that a
    // draft can never fail at model binding, where the 400 would name no field and collect no siblings.
    private static object FullDraft() => new
    {
        contact = new
        {
            fullName = "Jane Candidate",
            email = "Jane@Example.com",
            phoneNumber = "+541155550123",
            location = "Buenos Aires",
            website = "https://jane.example.com",
            summary = "Backend engineer.",
            profiles = new[]
            {
                new { network = "GitHub", username = "janedev", url = "https://github.com/janedev" }
            }
        },
        experiences = new[]
        {
            new
            {
                type = "Professional",
                organization = "Mercado Libre",
                position = "Senior Engineer",
                start = "2019-03-01",
                end = "2023-06-30",
                summary = "Payments platform.",
                highlights = new[] { "Cut latency in half" }
            }
        },
        educations = new[]
        {
            new
            {
                institution = "Universidad de Buenos Aires",
                degree = "Ingeniero en Sistemas",
                fieldOfStudy = "Software",
                start = "2012-03-01",
                end = "2017-12-01",
                grade = "8.4",
                level = "Bachelor"
            }
        },
        skills = new[] { new { name = "C#", level = "Advanced", yearsOfExperience = "7" } },
        projects = new[]
        {
            new
            {
                name = "buildcv",
                start = "2024-01-01",
                description = "A CV scorer.",
                repositoryUrl = "https://github.com/janedev/buildcv",
                liveDemoUrl = "https://buildcv.example.com",
                technologies = new[] { "dotnet" },
                highlights = new[] { "Deterministic scoring" }
            }
        },
        certificates = new[]
        {
            new
            {
                name = "AWS Solutions Architect",
                issuer = "Amazon",
                credentialId = "cred-123",
                credentialUrl = "https://aws.example.com/cred-123",
                validityStart = "2024-01-01",
                validityEnd = "2027-01-01"
            }
        },
        languages = new[] { new { name = "Español", fluency = "Bilingüe", level = "Native" } },
        awards = new[] { new { title = "Best Hack", awarder = "Hackathon AR", date = "2021-11-05", summary = "First place." } },
        publications = new[]
        {
            new
            {
                title = "On Scoring",
                publisher = "ACM",
                url = "https://acm.example.com/on-scoring",
                releaseDate = "2022-05-01",
                summary = "A paper."
            }
        },
        interests = new[] { new { name = "Climbing", keywords = new[] { "bouldering" } } },
        references = new[]
        {
            new
            {
                name = "John Manager",
                position = "Engineering Manager",
                company = "Mercado Libre",
                email = "john@example.com",
                phoneNumber = "+541155550999",
                referenceText = "Would hire again."
            }
        }
    };

    private static async Task<HttpResponseMessage> PostImportAsync(HttpClient client, string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = JsonContent.Create(body),
        }.WithBearer(token);

        return await client.SendAsync(request);
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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

public sealed class ScoringEndpointTests
{
    // The visibility rule at the HTTP boundary. The handler returns the literal "Forbidden.", and
    // ResultExtensions is what turns that one string into a 403 rather than the 400 every other
    // failure gets — so the mapping is worth one end-to-end assertion instead of being trusted.
    //
    // The posting here is never published, so the candidate is a stranger to a draft: exactly the
    // request that used to return 200 with a full breakdown of a recruiter's unreleased requirements.
    [Fact]
    public async Task Score_AgainstAnotherAccountsUnpublishedPosting_Returns403()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await CreateResumeAsync(client, candidateToken);
        var jobId = await CreateJobAsync(client, recruiterToken);

        var response = await ScoreAsync(client, candidateToken, resumeId, jobId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Score_AgainstAPublishedPosting_Succeeds()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await CreateResumeAsync(client, candidateToken);
        var jobId = await CreateJobAsync(client, recruiterToken);
        await PublishAsync(client, recruiterToken, jobId);

        var response = await ScoreAsync(client, candidateToken, resumeId, jobId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // The live shape, end to end. ScoringContractTests pins the mapping in isolation; this is the
    // assertion that the mapping is the one actually wired into the endpoint.
    [Fact]
    public async Task Score_ReturnsRecommendationsWithNamedEnumsAndAnImpactOnTheZeroToOneScale()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await CreateResumeAsync(client, candidateToken);
        var jobId = await CreateJobAsync(client, recruiterToken);
        await PublishAsync(client, recruiterToken, jobId);

        var response = await ScoreAsync(client, candidateToken, resumeId, jobId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var recommendations = json.RootElement.GetProperty("recommendations").EnumerateArray().ToList();

        // THE MOST VISIBLE SCORE MOVEMENT IN THE RELEASE, and asserted FIRST on purpose.
        //
        // This exact request returned 28 before renormalization and returns 0 now. The 28 was two
        // fabricated neutral halves: the posting states no skill and no language requirement, so both
        // sections were handed 0.5 and 0.45*0.5 + 0.10*0.5 = 0.275 of score arrived from questions
        // nobody asked. An empty resume against a posting demanding nothing genuinely matches nothing,
        // and the four sections that do apply all score zero.
        //
        // It leads the test because a full revert of renormalization moves the priorities below TOO, and
        // only the first failing assertion is ever observed — asserted last, this line could never be
        // the one that goes red, which makes it documentation rather than a guard.
        json.RootElement.GetProperty("overallScore").GetInt32().Should().Be(0,
            "the previous 28 came entirely from two unasked sections scoring a fabricated 0.5");

        // An empty resume is below the cap on education, certifications and projects, and the posting
        // states no skill or language requirement — so exactly those three fire.
        recommendations.Select(r => r.GetProperty("kind").GetString()).Should().Equal(
            "NoEducationRecorded", "FewerCertificationsThanExpected", "FewerProjectsThanExpected");

        // Two Importants rather than one Important and one NiceToHave, and the reason is renormalization:
        // with Skills and Languages asked nothing and weighted out, the four remaining sections are
        // scored out of 0.45, so Projects rises from 0.05 to 0.05/0.45 = 0.1111 and one more project is
        // worth 0.0370 instead of 0.0167 — over the 0.03 Important threshold. Advice about a section
        // that now carries more of the score is genuinely more important, which is the change working.
        recommendations.Select(r => r.GetProperty("priority").GetString()).Should().Equal(
            "Critical", "Important", "Important");
        recommendations.Select(r => r.GetProperty("impact").GetDouble()).Should().BeInDescendingOrder();

        foreach (var recommendation in recommendations)
        {
            recommendation.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
            recommendation.GetProperty("impact").GetDouble().Should().BeInRange(0.0, 1.0);
        }

        var weights = json.RootElement.GetProperty("breakdown").GetProperty("weights");
        weights.GetProperty("schemaVersion").GetInt32().Should().Be(2);

        // THE WEIGHTS ON THE WIRE ARE THE ONES THE SCORE WAS COMPUTED UNDER, not the defaults. This is
        // the same set persisted on the analysis, which is what keeps a past score self-explaining: a
        // client can multiply these six by the six section scores and get back the weightedTotal it was
        // shown. Serving Default() here while scoring under something else would make every historical
        // row a set of numbers that does not add up.
        weights.GetProperty("skills").GetDouble().Should().Be(0.0, "the posting stated no skill requirement");
        weights.GetProperty("languages").GetDouble().Should().Be(0.0, "nor any language requirement");
        weights.GetProperty("experience").GetDouble().Should().BeApproximately(0.20 / 0.45, 1e-9);
        weights.GetProperty("education").GetDouble().Should().BeApproximately(0.10 / 0.45, 1e-9);

        new[] { "skills", "experience", "education", "certifications", "projects", "languages" }
            .Sum(name => weights.GetProperty(name).GetDouble())
            .Should().BeApproximately(1.0, 1e-9);

        // The wire contract for the one field that predates this chain, asserted against the APP's
        // serializer rather than a test-local one. `band` is an int on the DTO, so this is a statement
        // about what clients receive and NOT evidence about converter registration — nothing in this
        // response is an enum type any more, which is exactly what makes it converter-proof.
        json.RootElement.GetProperty("band").ValueKind.Should().Be(JsonValueKind.Number);
        json.RootElement.GetProperty("breakdown").GetProperty("sections")[0]
            .GetProperty("section").GetString().Should().Be("Skills",
                "every SectionType on the wire is a name, in both arrays that carry one");
    }

    // The pre-existing Enum.TryParse hole on ExperienceType, followed through to what a candidate sees
    // rather than left as a note. `Enum.TryParse` has no Enum.IsDefined guard on this endpoint (the two
    // level fields were guarded in the previous PR; the four older sites were correctly left to their
    // own behaviour-changing PR), so "99" is accepted and stored as a value that is neither member.
    //
    // It fails CLOSED in the score — ComputeExperienceScore tests `== Professional`, so the entry is
    // simply not counted, and only the candidate who sent it is affected. What is new is that the same
    // entry now also fails `!= Professional` in the recommendation rule, so instead of a silent
    // deduction the candidate is told the entry's type is why the time is not counted. Executed here
    // rather than asserted from reading, because "arguably the right outcome" is not evidence.
    [Fact]
    public async Task Score_AnExperienceTypeTheEnumDoesNotKnow_BecomesAdviceRatherThanASilentDeduction()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await CreateResumeAsync(client, candidateToken);

        using var addExperience = new HttpRequestMessage(HttpMethod.Post, $"/resumes/{resumeId}/experiences")
        {
            Content = JsonContent.Create(new
            {
                type = "99",
                organization = "Acme",
                position = "Backend Developer",
                start = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3),
                end = (DateOnly?)null,
                summary = (string?)null
            })
        }.WithBearer(candidateToken);

        (await client.SendAsync(addExperience)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the undefined numeric value is accepted today — that is the pre-existing hole, not this test's claim");

        var jobId = await CreateJobAsync(client, recruiterToken);
        await PublishAsync(client, recruiterToken, jobId);

        var response = await ScoreAsync(client, candidateToken, resumeId, jobId);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        json.RootElement.GetProperty("breakdown").GetProperty("experienceScore").GetDouble()
            .Should().Be(0.0, "an undefined type is not Professional, so the time fails closed");

        json.RootElement.GetProperty("recommendations").EnumerateArray()
            .Select(r => r.GetProperty("kind").GetString())
            .Should().Contain("ExperienceNotMarkedProfessional",
                "the entry becomes advice naming the fix instead of a deduction with no explanation");
    }

    internal static async Task<Guid> CreateResumeAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes")
        {
            Content = JsonContent.Create(new
            {
                fullName = "Jane Candidate",
                email = "jane@example.com",
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

    internal static async Task<Guid> CreateJobAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/jobs")
        {
            Content = JsonContent.Create(new
            {
                title = "Senior Backend Engineer",
                companyName = "Contoso",
                companyId = (Guid?)null,
                description = "Build deterministic scoring systems."
            })
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetProperty("value").GetGuid();
    }

    internal static async Task PublishAsync(HttpClient client, string token, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/jobs/{jobId}/publish").WithBearer(token);
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    internal static async Task<HttpResponseMessage> ScoreAsync(
        HttpClient client, string token, Guid resumeId, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/scoring/score")
        {
            Content = JsonContent.Create(new { resumeId, jobPostingId = jobId })
        }.WithBearer(token);

        return await client.SendAsync(request);
    }
}

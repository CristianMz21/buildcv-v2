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
        // THE NUMBER A RELEASE NOTE SHOULD QUOTE IS 22, NOT 28, because 28 is a value only a mid-chain
        // build ever produced. On main (a7cb736) this exact request scored 22: five sections, no
        // Languages term, an empty resume, and a posting stating no requirement, so Skills alone was
        // handed a fabricated neutral 0.5 -- 0.45 * 0.5 = 0.225, and (int)Math.Round(22.5) is 22 under
        // the banker's rounding Analysis.OverallScore uses. That was run rather than reasoned about;
        // the pre-change capture in pr-3-report.md shows the same 0.225 / 22 off the live endpoint.
        //
        // The 28 arrives only after Languages is weighted 0.10 and before renormalization lands, when
        // BOTH unasked sections are handed 0.5: 0.45*0.5 + 0.10*0.5 = 0.275 -> 28. Quoting it as the
        // "before" measures this release against a weighting that never shipped anywhere.
        //
        // It returns 0 now, and that is the fabrication removed rather than points lost: the posting
        // states no skill and no language requirement, so neither section is scored at all, and an
        // empty resume genuinely matches nothing in the four that do apply.
        //
        // It leads the test because a full revert of renormalization moves the priorities below TOO, and
        // only the first failing assertion is ever observed — asserted last, this line could never be
        // the one that goes red, which makes it documentation rather than a guard.
        json.RootElement.GetProperty("overallScore").GetInt32().Should().Be(0,
            "22 on main and 28 mid-chain both came entirely from unasked sections scoring a fabricated 0.5");

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
        // An analysis written after the bump reports the new model version, on the wire, from a live
        // request — which is where a client reads it.
        weights.GetProperty("schemaVersion").GetInt32().Should().Be(3,
            "v3 is the release in which IsSatisfiedBy started consulting the skill lexicon");

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

        // The v1 wire contract for `band`, asserted against the APP's serializer rather than a
        // test-local one: the ScoreBand NAME, consistent with every other enum in the response. It is
        // a string on the DTO, so this is a statement about what clients receive and NOT evidence
        // about converter registration — nothing in this response is an enum type, which is exactly
        // what makes it converter-proof. An overallScore of 0 sits in the lowest band.
        json.RootElement.GetProperty("band").GetString().Should().Be("Low");
        json.RootElement.GetProperty("breakdown").GetProperty("sections")[0]
            .GetProperty("section").GetString().Should().Be("Skills",
                "every SectionType on the wire is a name, in both arrays that carry one");
    }

    // An entry the score does not count becomes advice naming the fix, followed through to what a
    // candidate actually receives rather than left as a note.
    //
    // THIS TEST USED TO SEND type = "99". It could, because the endpoint parsed ExperienceType with
    // Enum.TryParse and no Enum.IsDefined guard, so an undefined value was accepted and stored; the
    // test asserted the 200 explicitly and called it a pre-existing hole. Issue #21 closed the hole,
    // so that request is now a 400 and no new resume can hold such a row — the refusal is pinned by
    // ResumeLevelFieldsTests.AddExperience_WithATypeTheEnumDoesNotKnow_IsABadRequest, and the claim
    // that a row already holding one still counts as unmarked experience moved to
    // RecommendationBuilderTests, where the aggregate can be built directly.
    //
    // What is left here is the reachable half, and it is the one worth having end to end: Volunteer is
    // a defined type that ComputeExperienceScore does not count, and the wire contract has to carry
    // the recommendation kind that explains why.
    [Fact]
    public async Task Score_AnExperienceNotMarkedProfessional_BecomesAdviceRatherThanASilentDeduction()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await CreateResumeAsync(client, candidateToken);

        using var addExperience = new HttpRequestMessage(HttpMethod.Post, $"/v1/resumes/{resumeId}/experiences")
        {
            Content = JsonContent.Create(new
            {
                type = "Volunteer",
                organization = "Acme",
                position = "Backend Developer",
                start = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3),
                end = (DateOnly?)null,
                summary = (string?)null
            })
        }.WithBearer(candidateToken);

        (await client.SendAsync(addExperience)).StatusCode.Should().Be(HttpStatusCode.OK);

        var jobId = await CreateJobAsync(client, recruiterToken);
        await PublishAsync(client, recruiterToken, jobId);

        var response = await ScoreAsync(client, candidateToken, resumeId, jobId);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        json.RootElement.GetProperty("breakdown").GetProperty("experienceScore").GetDouble()
            .Should().Be(0.0, "Volunteer is not Professional, so the time is not counted");

        json.RootElement.GetProperty("recommendations").EnumerateArray()
            .Select(r => r.GetProperty("kind").GetString())
            .Should().Contain("ExperienceNotMarkedProfessional",
                "the entry becomes advice naming the fix instead of a deduction with no explanation");
    }

    internal static async Task<Guid> CreateResumeAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes")
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
        return json.RootElement.GetProperty("id").GetGuid();
    }

    internal static async Task<Guid> CreateJobAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs")
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
        return json.RootElement.GetProperty("id").GetGuid();
    }

    internal static async Task PublishAsync(HttpClient client, string token, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/jobs/{jobId}/publish").WithBearer(token);
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    internal static async Task<HttpResponseMessage> ScoreAsync(
        HttpClient client, string token, Guid resumeId, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/scoring/score")
        {
            Content = JsonContent.Create(new { resumeId, jobPostingId = jobId })
        }.WithBearer(token);

        return await client.SendAsync(request);
    }
}

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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

public sealed class FullFlowTests
{
    [Fact]
    public async Task Register_CreateResume_CreateJob_Publish_Score_ReturnsAnalysis()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        using var createResume = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes")
        {
            Content = JsonContent.Create(new
            {
                fullName = "Jane Candidate",
                email = "jane@example.com",
                phoneNumber = (string?)null,
                location = (string?)null,
                summary = (string?)null
            })
        }.WithBearer(candidateToken);
        var resumeResponse = await client.SendAsync(createResume);
        resumeResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var resumeJson = JsonDocument.Parse(await resumeResponse.Content.ReadAsStringAsync());
        var resumeId = resumeJson.RootElement.GetProperty("id").GetGuid();

        using var createJob = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs")
        {
            Content = JsonContent.Create(new
            {
                title = "Senior Backend Engineer",
                companyName = "Contoso",
                companyId = (Guid?)null,
                description = "Build deterministic scoring systems."
            })
        }.WithBearer(recruiterToken);
        var jobResponse = await client.SendAsync(createJob);
        jobResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var jobJson = JsonDocument.Parse(await jobResponse.Content.ReadAsStringAsync());
        var jobId = jobJson.RootElement.GetProperty("id").GetGuid();

        using var publish = new HttpRequestMessage(HttpMethod.Post, $"/v1/jobs/{jobId}/publish")
            .WithBearer(recruiterToken);
        (await client.SendAsync(publish)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var score = new HttpRequestMessage(HttpMethod.Post, "/v1/scoring/score")
        {
            Content = JsonContent.Create(new { resumeId, jobPostingId = jobId })
        }.WithBearer(candidateToken);
        var scoreResponse = await client.SendAsync(score);

        scoreResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var analysisJson = JsonDocument.Parse(await scoreResponse.Content.ReadAsStringAsync());
        var overallScore = analysisJson.RootElement.GetProperty("overallScore").GetInt32();
        overallScore.Should().BeInRange(0, 100);
    }
}

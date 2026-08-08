using System.Net;
using System.Net.Http.Json;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Observability;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

// The counters, driven end to end through the routes that emit them. Every assertion here is on a
// TAG as well as on a count, because the tag is the half that can go wrong in a way nothing else
// notices: a counter that stops incrementing shows up as a flat graph, while a tag carrying an
// account id shows up as a metrics backend nobody realised was holding PII.
public sealed class ObservabilityMetricsTests
{
    // The counter M1's de-duplication is invisible without. Both outcomes come from ONE test, because
    // the interesting claim is that the second identical request is tagged DIFFERENTLY from the first
    // — two separate tests could each pass while the handler emitted the same value twice.
    [Fact]
    public async Task ScoringTheSamePairTwice_CountsOneComputedRunAndOneDeduplicatedOne()
    {
        using var factory = new ApiTestFactory();
        using var recorder = new MeasurementRecorder(factory.Services.GetRequiredService<BuildCvMetrics>());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await ScoringEndpointTests.CreateResumeAsync(client, candidateToken);
        var jobId = await ScoringEndpointTests.CreateJobAsync(client, recruiterToken);
        await ScoringEndpointTests.PublishAsync(client, recruiterToken, jobId);

        (await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        recorder.TagValuesOf(BuildCvMetrics.ScoringRunsInstrument, BuildCvMetrics.OutcomeTag)
            .Should().Equal(ScoringOutcomes.Computed, ScoringOutcomes.Deduplicated);
    }

    [Fact]
    public async Task EachReadabilityRun_CountsOneReport()
    {
        using var factory = new ApiTestFactory();
        using var recorder = new MeasurementRecorder(factory.Services.GetRequiredService<BuildCvMetrics>());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var resumeId = await ScoringEndpointTests.CreateResumeAsync(client, token);

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"/v1/resumes/{resumeId}/readability").WithBearer(token);
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Three, not "at least one": readability has NO de-duplication (see the note on
        // EvaluateResumeReadabilityHandler), so a run per request is the behaviour, and a count that
        // fell behind would be the first sign that had silently changed.
        recorder.Measurements
            .Where(measurement => measurement.Instrument == BuildCvMetrics.ReadabilityReportsInstrument)
            .Sum(measurement => measurement.Value)
            .Should().Be(3);
    }

    // The named-policy half of the throttle counter. Six auth requests against a 5/min window: the
    // sixth is refused, and the tag has to name the policy attached to the endpoint.
    [Fact]
    public async Task ANamedPolicyRejection_IsTaggedWithThatPolicy()
    {
        using var factory = new ApiTestFactory();
        using var recorder = new MeasurementRecorder(factory.Services.GetRequiredService<BuildCvMetrics>());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
            last = await client.PostAsJsonAsync(
                "/v1/auth/login", new { email = $"nobody{i}@example.com", password = TestHelpers.Password });

        last!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the 5/min auth window has to be the thing that refused, or the tag below means nothing");

        recorder.TagValuesOf(BuildCvMetrics.ThrottleRejectionsInstrument, BuildCvMetrics.PolicyTag)
            .Should().Equal(ThrottlePolicies.Auth);
    }

    // The other half, and the one that makes the first half evidence rather than a tautology: a route
    // with NO rate-limiting metadata falls back to the global limiter's name. Without this, the tag
    // could be a constant "auth" and the test above would still pass.
    //
    // The global window is 100/min and every TestServer request shares one partition, so the two auth
    // calls above count toward it — 120 requests to a route with no policy of its own is comfortably
    // past it.
    [Fact]
    public async Task AGlobalLimiterRejection_IsTaggedGlobal()
    {
        using var factory = new ApiTestFactory();
        using var recorder = new MeasurementRecorder(factory.Services.GetRequiredService<BuildCvMetrics>());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        HttpResponseMessage? last = null;
        for (var i = 0; i < 120; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/resumes").WithBearer(token);
            last = await client.SendAsync(request);
            if (last.StatusCode == HttpStatusCode.TooManyRequests)
                break;
        }

        last!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        recorder.TagValuesOf(BuildCvMetrics.ThrottleRejectionsInstrument, BuildCvMetrics.PolicyTag)
            .Should().OnlyContain(policy => policy == ThrottlePolicies.Global)
            .And.NotBeEmpty();
    }

    // A per-account limiter, refused INSIDE the endpoint after the middleware has already let the
    // request through — so this is the branch RateLimiterOptions.OnRejected never sees, and the one a
    // reader would most reasonably assume was covered by it.
    [Fact]
    public async Task APerAccountRejection_IsTaggedWithItsOwnLimiter()
    {
        using var factory = new ApiTestFactory();
        using var recorder = new MeasurementRecorder(factory.Services.GetRequiredService<BuildCvMetrics>());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        HttpResponseMessage? last = null;
        for (var i = 0; i <= ResumeImportRateLimiter.PermitLimit; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
            {
                Content = JsonContent.Create(new
                {
                    contact = new { fullName = "Jane Candidate", email = "jane@example.com" }
                })
            }.WithBearer(token);
            last = await client.SendAsync(request);
        }

        last!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        recorder.TagValuesOf(BuildCvMetrics.ThrottleRejectionsInstrument, BuildCvMetrics.PolicyTag)
            .Should().Equal(ThrottlePolicies.ResumeImport);
    }

    // The cardinality rule, executed. Every tag value any of these paths emits has to come from a set
    // named in code — that is what keeps a metrics backend from becoming an unencrypted store of
    // account ids and candidate text, and what keeps the series count bounded by the number of code
    // paths rather than by the number of users.
    [Fact]
    public async Task EveryTagValueTheseRoutesEmit_ComesFromAClosedSet()
    {
        using var factory = new ApiTestFactory();
        using var recorder = new MeasurementRecorder(factory.Services.GetRequiredService<BuildCvMetrics>());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await ScoringEndpointTests.CreateResumeAsync(client, candidateToken);
        var jobId = await ScoringEndpointTests.CreateJobAsync(client, recruiterToken);
        await ScoringEndpointTests.PublishAsync(client, recruiterToken, jobId);
        await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);

        using (var readability = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/resumes/{resumeId}/readability").WithBearer(candidateToken))
        {
            await client.SendAsync(readability);
        }

        recorder.Measurements.Should().NotBeEmpty();

        foreach (var measurement in recorder.Measurements)
        {
            foreach (var tag in measurement.Tags)
            {
                tag.Key.Should().BeOneOf(
                    BuildCvMetrics.OutcomeTag, BuildCvMetrics.ReasonTag, BuildCvMetrics.PolicyTag);

                var value = tag.Value?.ToString();
                var allowed = ScoringOutcomes.All.Concat(ThrottlePolicies.All).ToList();
                allowed.Should().Contain(value,
                    "'{0}' on {1} is not a value this repository declared", value, measurement.Instrument);
            }
        }
    }
}

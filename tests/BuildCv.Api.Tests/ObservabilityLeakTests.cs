using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BuildCv.Application.Common.Observability;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BuildCv.Api.Tests;

// THE ONE TEST IN THIS MILESTONE THAT PROTECTS SOMETHING IRREVERSIBLE.
//
// This API now handles whole CVs, the raw text extracted from an uploaded document, job offers, and
// readability advice that quotes a candidate's own bullet points back at them. Columns holding that
// material are AES-GCM sealed at rest and the advice is sealed under its own context string — and a
// log line, a metric tag and a span attribute are covered by NONE of that. Worse, they go somewhere
// the database does not: a log aggregator, a metrics backend, a trace exporter, each with its own
// retention and its own access list. A leaked row can be re-encrypted; a leaked log line has already
// been shipped, indexed and replicated by the time anyone notices.
//
// So: drive every path that touches candidate material with recognisable sentinel text, capture
// everything the process wrote to every observability channel, and assert the sentinels are absent.
//
// WHAT MAKES THIS MORE THAN A GREEN LIGHT. An absence assertion is the easiest kind of test to write
// so that it cannot fail, so three things are asserted alongside it:
//
//   1. Every request is checked for SUCCESS and its sentinel is checked to have come BACK in the
//      response body. A suite of 400s would carry no CV content anywhere and would pass this test
//      while proving nothing. The response is the one place the material is supposed to appear.
//   2. The log filters are cleared to Trace and the recorder is proved to have seen framework
//      Debug/Trace lines (TheRecorderSeesTraceLevelFrameworkLines below). An absence assertion over a
//      log filtered to Warning certifies the levels nobody was worried about.
//   3. The ACTIVITY recorder listens to every source, not just this repository's — a span this code
//      never wrote is exactly where an unnoticed leak would sit.
//
// The mandatory negative control for this test is to make one path log a sentinel and confirm it
// reds; see the milestone report for the run.
public sealed class ObservabilityLeakTests
{
    // Alphabetic and distinctive, so that a failure names the field rather than "something leaked".
    // Alphabetic because these have to survive the Domain's own value-object rules — a name, a
    // technology and an organization all refuse punctuation this test has no interest in.
    private const string FullName = "Zzqfullname";
    private const string Location = "Zzqlocation";
    private const string Summary = "Zzqsummary";
    private const string Organization = "Zzqorganization";
    private const string Position = "Zzqposition";
    private const string Highlight = "Zzqhighlight";
    private const string Skill = "Zzqskill";
    private const string Institution = "Zzqinstitution";
    private const string Certificate = "Zzqcertificate";
    private const string Interest = "Zzqinterest";
    private const string Fluency = "Zzqfluency";
    private const string JobTitle = "Zzqjobtitle";
    private const string JobDescription = "Zzqjobdescription";
    private const string UploadedText = "Zzquploadedtext";
    private const string UploadedFileName = "Zzqfilename";
    private const string CorruptDocument = "Zzqcorruptdocument";
    private const string RejectedFieldValue = "Zzqrejectedvalue";
    private const string OfferText = "Zzqoffertext";
    private const string Email = "zzqemail@example.com";

    private static readonly string[] Sentinels =
    [
        FullName, Location, Summary, Organization, Position, Highlight, Skill, Institution,
        Certificate, Interest, Fluency, JobTitle, JobDescription, UploadedText, UploadedFileName,
        CorruptDocument, RejectedFieldValue, OfferText, "zzqemail"
    ];

    [Fact]
    public async Task NoCvContentReachesALogScopeAMetricTagOrASpan()
    {
        var logs = new RecordingLoggerProvider();
        using var spans = new ActivityRecorder();
        using var factory = new ApiTestFactory(configureServices: RecordingLogging.Capturing(logs));
        using var measurements = new MeasurementRecorder(factory.Services.GetRequiredService<BuildCvMetrics>());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var (_, candidate) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiter) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        await DriveExtractAsync(client, candidate);
        await DriveFailingExtractAsync(client, candidate);
        await DriveProposeAsync(client, candidate);
        var resumeId = await DriveImportAsync(client, candidate);
        await DriveRejectedImportAsync(client, candidate);
        await DriveScoreAsync(client, candidate, recruiter, resumeId);
        await DriveReadabilityAsync(client, candidate);
        await DriveJobOfferAsync(client, candidate);

        // One scope over all three, so a run that leaks into two channels reports both. Without it the
        // first failure throws and the second channel is never looked at — which is how a fix aimed at
        // the reported half gets called done.
        using (new AssertionScope())
        {
            AssertNoSentinelIn("a log line, its state or its scopes", logs.AllText);
            AssertNoSentinelIn("a metric instrument name or tag", measurements.AllText);
            AssertNoSentinelIn("an Activity name, tag, event or baggage item", spans.AllText);
        }

        // Not decoration: without them the three assertions above would still pass against a recorder
        // that captured nothing, which is the failure mode this whole test is most exposed to.
        logs.Records.Should().NotBeEmpty();
        measurements.Measurements.Should().NotBeEmpty();
        spans.Activities.Should().NotBeEmpty();
    }

    // The control for the control. RecordingLogging.Capturing clears the host's filter rules so that
    // nothing this recorder is asked to record is dropped before it arrives — and appsettings.json
    // filters Microsoft.AspNetCore to Warning, which would hide the framework's own Debug and Trace
    // lines about routing, binding and authentication. Those are precisely the lines a leak would hide
    // in, so an absence assertion that never saw them would be measuring the wrong log.
    [Fact]
    public async Task TheRecorderSeesTraceLevelFrameworkLines()
    {
        var logs = new RecordingLoggerProvider();
        using var factory = new ApiTestFactory(configureServices: RecordingLogging.Capturing(logs));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        await client.GetAsync("/health/live");

        logs.Records.Should().Contain(
            record => record.Level == LogLevel.Trace
                && record.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
            "appsettings filters Microsoft.AspNetCore to Warning, so seeing a Trace line is what proves "
            + "the recorder is looking at an unfiltered log rather than a curated one");
    }

    private static async Task DriveExtractAsync(HttpClient client, string token)
    {
        var response = await UploadAsync(
            client, token, "/v1/resumes/import/extract",
            Encoding.UTF8.GetBytes($"Curriculum\n{UploadedText}\n{Organization}"), "text/plain");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(UploadedText,
            "the extracted text has to come back, or nothing candidate-shaped ever entered the process");
    }

    // The refusal path, which is the one where a library's own message is most likely to be forwarded:
    // the bytes here are a broken PDF, and PdfPig's exception message is written about the document's
    // internals. The adapter is documented to forward none of it; this executes that.
    private static async Task DriveFailingExtractAsync(HttpClient client, string token)
    {
        var response = await UploadAsync(
            client, token, "/v1/resumes/import/extract",
            Encoding.UTF8.GetBytes($"%PDF-1.7\n{CorruptDocument}\nnot really a pdf"), "application/pdf");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(CorruptDocument,
            "the refusal message is this repository's own words, never the parser's");
    }

    private static async Task DriveProposeAsync(HttpClient client, string token)
    {
        var response = await UploadAsync(
            client, token, "/v1/resumes/import/propose",
            Encoding.UTF8.GetBytes(
                $"{FullName}\n{Email}\n\nEXPERIENCIA\n{Position} - {Organization}\n{UploadedText}"),
            "text/plain");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(FullName,
            "the proposed draft has to carry the parsed name, or this path drove nothing");
    }

    private static async Task<Guid> DriveImportAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = JsonContent.Create(SentinelDraft())
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        body.Should().Contain(FullName);

        using var created = JsonDocument.Parse(body);
        return created.RootElement.GetProperty("id").GetGuid();
    }

    // The field-error path. It builds its verdicts by calling the real Domain factories and catching
    // what they threw, so a Domain message that quoted its input would travel out through the errors
    // object — and, if anything logged that object, straight into a log.
    private static async Task DriveRejectedImportAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = JsonContent.Create(new
            {
                contact = new { fullName = FullName, email = Email },
                experiences = new[]
                {
                    new
                    {
                        type = "Professional",
                        organization = Organization,
                        position = Position,
                        start = RejectedFieldValue
                    }
                }
            })
        }.WithBearer(token);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("experiences[0].start",
            "the refusal has to be the field-error path, not a binding failure that never saw the value");
    }

    private static async Task DriveScoreAsync(
        HttpClient client, string candidateToken, string recruiterToken, Guid resumeId)
    {
        using var job = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs")
        {
            Content = JsonContent.Create(new
            {
                title = JobTitle,
                companyName = "Contoso",
                companyId = (Guid?)null,
                description = JobDescription
            })
        }.WithBearer(recruiterToken);

        var created = await client.SendAsync(job);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var jobJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var jobId = jobJson.RootElement.GetProperty("id").GetGuid();

        await ScoringEndpointTests.PublishAsync(client, recruiterToken, jobId);

        // Twice, so both the computed and the de-duplicated branch of ScoreResumeHandler run — they
        // tag their span and their counter from different lines.
        for (var i = 0; i < 2; i++)
        {
            var scored = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
            scored.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    // Readability advice quotes the candidate's own material, which is why its Message column is
    // encrypted under its own context string. If any of it reached a log this would be the path.
    private static async Task DriveReadabilityAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes")
        {
            Content = JsonContent.Create(new
            {
                fullName = FullName,
                email = Email,
                phoneNumber = (string?)null,
                location = Location,
                summary = Summary
            })
        }.WithBearer(token);

        var created = await client.SendAsync(request);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var resumeId = json.RootElement.GetProperty("id").GetGuid();

        using var evaluate = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/resumes/{resumeId}/readability").WithBearer(token);
        var report = await client.SendAsync(evaluate);

        report.StatusCode.Should().Be(HttpStatusCode.OK);
        (await report.Content.ReadAsStringAsync()).Should().Contain("recommendations",
            "advice has to have been produced, or this path measured an empty report");
    }

    // The other half of the material this API now holds. A pasted job offer is somebody's employer's
    // text, and the candidate-owned draft built from it is theirs — neither belongs in a log any more
    // than a CV does.
    private static async Task DriveJobOfferAsync(HttpClient client, string token)
    {
        using var extract = new HttpRequestMessage(HttpMethod.Post, "/v1/job-offers/extract")
        {
            Content = JsonContent.Create(new { text = $"{OfferText}. Our stack is C# and Docker." })
        }.WithBearer(token);

        var extracted = await client.SendAsync(extract);
        extracted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await extracted.Content.ReadAsStringAsync()).Should().Contain("Docker",
            "the offer text has to have been parsed, or this path drove nothing");

        using var import = new HttpRequestMessage(HttpMethod.Post, "/v1/job-offers/import")
        {
            Content = JsonContent.Create(new
            {
                title = JobTitle,
                companyName = "Contoso",
                description = JobDescription,
                requirements = new[] { new { skill = "C#", priority = "MustHave" } }
            })
        }.WithBearer(token);

        var imported = await client.SendAsync(import);
        var body = await imported.Content.ReadAsStringAsync();
        imported.StatusCode.Should().Be(HttpStatusCode.Created, body);
        body.Should().Contain(JobTitle);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string token, string route, byte[] bytes, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        // The FILE NAME is a sentinel too. A CV is routinely called "Jane_Doe_CV.pdf", so the name is
        // candidate-identifying in its own right, and it is a value the framework's form reader sees
        // before any of this repository's code does.
        content.Add(file, "file", $"{UploadedFileName}.txt");

        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content }
            .WithBearer(token);
        return await client.SendAsync(request);
    }

    private static object SentinelDraft() => new
    {
        contact = new
        {
            fullName = FullName,
            email = Email,
            location = Location,
            summary = Summary
        },
        experiences = new[]
        {
            new
            {
                type = "Professional",
                organization = Organization,
                position = Position,
                start = "2019-03-01",
                end = "2023-06-30",
                summary = Summary,
                highlights = new[] { Highlight }
            }
        },
        educations = new[]
        {
            new { institution = Institution, fieldOfStudy = "Software", start = "2012-03-01", level = "Bachelor" }
        },
        skills = new[] { new { name = Skill, level = "Advanced", yearsOfExperience = "7" } },
        certificates = new[] { new { name = Certificate, issuer = "Zzqissuer" } },
        languages = new[] { new { name = "Zzqlanguagename", fluency = Fluency, level = "Native" } },
        interests = new[] { new { name = Interest, keywords = new[] { "Zzqkeyword" } } }
    };

    private static void AssertNoSentinelIn(string channel, IReadOnlyList<string> captured)
    {
        var leaks = captured
            .SelectMany(text => Sentinels
                .Where(sentinel => text.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
                .Select(sentinel => $"'{sentinel}' in: {Truncate(text)}"))
            .Distinct()
            .ToList();

        leaks.Should().BeEmpty(
            "no part of a CV, an uploaded document or a job offer may reach {0} — {1} occurrence(s): {2}",
            channel, leaks.Count, string.Join(" | ", leaks));
    }

    private static string Truncate(string text) =>
        text.Length <= 300 ? text : text[..300] + "...";
}

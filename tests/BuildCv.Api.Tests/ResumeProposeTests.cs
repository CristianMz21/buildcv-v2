using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildCv.Api.Security;
using FluentAssertions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BuildCv.Api.Tests;

// POST /resumes/import/propose over the real pipeline: auth, CSRF, throttling, and the proposed draft +
// confidence themselves. Every fixture is built in-test.
public sealed class ResumeProposeTests
{
    private const string PdfContentType = "application/pdf";

    [Fact]
    public async Task Propose_APdf_ReturnsAPopulatedDraftAndSeparateConfidence()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostProposeAsync(client, token, Pdf(
            "Jane Doe",
            "jane.doe@example.com",
            "",
            "SKILLS",
            "Python, SQL"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var contact = body.RootElement.GetProperty("draft").GetProperty("contact");
        contact.GetProperty("fullName").GetString().Should().Be("Jane Doe");
        contact.GetProperty("email").GetString().Should().Be("jane.doe@example.com");

        var confidence = body.RootElement.GetProperty("confidence");
        confidence.GetProperty("overall").GetString().Should().NotBeNullOrEmpty();
        confidence.GetProperty("fields").GetArrayLength().Should().BeGreaterThan(0);

        // Confidence is a SEPARATE structure — it must not be smuggled onto the draft the client posts back.
        body.RootElement.GetProperty("draft").TryGetProperty("confidence", out _)
            .Should().BeFalse("confidence never crosses back on the submit type");
    }

    // THE PHASE'S CENTRAL GUARANTEE over the wire: proposing a draft creates NO resume. If anyone added
    // an "extract and save" shortcut here, GET /resumes would return one and this goes red. The only
    // writer is POST /resumes/import.
    [Fact]
    public async Task Propose_CreatesNoResume()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var propose = await PostProposeAsync(client, token, Pdf("Jane Doe", "jane.doe@example.com"));
        propose.StatusCode.Should().Be(HttpStatusCode.OK);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/resumes").WithBearer(token);
        var list = await client.SendAsync(listRequest);
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("items").GetArrayLength()
            .Should().Be(0, "proposing a draft must not create a resume");
    }

    // The proposed draft is directly submittable: post it straight back to the only writer and it creates
    // the resume. This is the whole design — the response draft IS the submit shape.
    [Fact]
    public async Task Propose_TheReturnedDraft_RoundTripsToImport()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var propose = await PostProposeAsync(client, token, Pdf("Jane Doe", "jane.doe@example.com"));
        using var proposed = JsonDocument.Parse(await propose.Content.ReadAsStringAsync());
        var draftJson = proposed.RootElement.GetProperty("draft").GetRawText();

        using var importRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = new StringContent(draftJson, Encoding.UTF8, "application/json"),
        }.WithBearer(token);
        var import = await client.SendAsync(importRequest);

        import.StatusCode.Should().Be(HttpStatusCode.Created, await import.Content.ReadAsStringAsync());
    }

    // Absent AND flagged, both halves over the wire: a CV with no phone leaves the draft field null AND
    // reports a NotExtracted provenance for it.
    [Fact]
    public async Task Propose_AFieldNotFound_IsNullOnTheDraftAndFlaggedInConfidence()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostProposeAsync(client, token, Pdf("Jane Doe", "jane.doe@example.com"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var contact = body.RootElement.GetProperty("draft").GetProperty("contact");
        contact.GetProperty("phoneNumber").ValueKind.Should().Be(JsonValueKind.Null, "the CV states no phone");

        var phoneField = body.RootElement.GetProperty("confidence").GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("path").GetString() == "contact.phoneNumber");
        phoneField.GetProperty("confidence").GetString()
            .Should().Be("NotExtracted", "the review screen must be told the parser looked and found nothing");
    }

    // A two-column upload is warned about and drops overall confidence to Low — never silently reordered.
    [Fact]
    public async Task Propose_ATwoColumnPdf_WarnsAboutTheLayout()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostProposeAsync(client, token, TwoColumnPdf(
            contactRows: ["Diego Torres", "diego@example.com"],
            leftRows: ["SKILLS", "Python", "Django", "Postgres", "Redis", "Celery"],
            rightRows: ["EXPERIENCE", "Backend Dev", "at Mercado", "2018 to 2022", "built the API", "on call"]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var confidence = body.RootElement.GetProperty("confidence");
        confidence.GetProperty("overall").GetString().Should().Be("Low");
        confidence.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain(w => w!.Contains("two-column"));
    }

    [Fact]
    public async Task Propose_Unauthenticated_Is401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import/propose")
        {
            Content = Upload(Encoding.UTF8.GetBytes("text"), "text/plain", "cv.txt"),
        };

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // The CSRF pair, as pinned for /extract and /import: multipart does not bypass the guard, and the
    // route must never appear in CsrfGuardMiddleware.ExemptPaths.
    [Fact]
    public async Task Propose_FromACookieClientWithoutTheCsrfToken_IsForbidden()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import/propose")
        {
            Content = Upload(Encoding.UTF8.GetBytes("text"), "text/plain", "cv.txt"),
        };

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Propose_FromACookieClientWithTheCsrfToken_IsOk()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var csrfToken = await client.GetAntiforgeryTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import/propose")
        {
            Content = Upload(Encoding.UTF8.GetBytes("Jane Doe\njane@example.com"), "text/plain", "cv.txt"),
        };
        request.Headers.Add(CsrfGuardMiddleware.CsrfHeaderName, csrfToken);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Propose_BeyondItsPerAccountCeiling_IsThrottled()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        for (var attempt = 0; attempt < DocumentExtractionRateLimiter.PermitLimit; attempt++)
        {
            (await PostProposeAsync(client, token, Upload(Encoding.UTF8.GetBytes("cv"), "text/plain", "cv.txt")))
                .StatusCode.Should().Be(HttpStatusCode.OK, "attempt {0} is inside the window", attempt);
        }

        var throttled = await PostProposeAsync(client, token, Upload(Encoding.UTF8.GetBytes("cv"), "text/plain", "cv.txt"));

        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter.Should().NotBeNull();
    }

    private static async Task<HttpResponseMessage> PostProposeAsync(
        HttpClient client, string token, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import/propose")
        {
            Content = content,
        }.WithBearer(token);
        return await client.SendAsync(request);
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string contentType, string fileName)
    {
        var filePart = new ByteArrayContent(bytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { filePart, "file", fileName } };
    }

    private static MultipartFormDataContent Pdf(params string[] lines) =>
        Upload(PdfBytes(lines), PdfContentType, "cv.pdf");

    private static MultipartFormDataContent TwoColumnPdf(
        string[] contactRows, string[] leftRows, string[] rightRows)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var baseline = 790d;
        foreach (var row in contactRows)
        {
            page.AddText(row, 12, new PdfPoint(40, baseline), font);
            baseline -= 22;
        }

        baseline -= 22;
        for (var i = 0; i < leftRows.Length; i++)
            page.AddText(leftRows[i], 12, new PdfPoint(40, baseline - i * 22), font);
        for (var i = 0; i < rightRows.Length; i++)
            page.AddText(rightRows[i], 12, new PdfPoint(330, baseline - i * 22), font);

        return Upload(builder.Build(), PdfContentType, "cv.pdf");
    }

    private static byte[] PdfBytes(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var baseline = 790d;
        foreach (var line in lines)
        {
            if (line.Length > 0)
                page.AddText(line, 12, new PdfPoint(40, baseline), font);
            baseline -= 22;
        }

        return builder.Build();
    }
}

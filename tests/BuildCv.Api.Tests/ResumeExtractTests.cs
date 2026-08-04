using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildCv.Api.Security;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BuildCv.Api.Tests;

// POST /resumes/import/extract over the real pipeline: auth, CSRF, throttling, and the extraction
// answers themselves. Every fixture is built in-test — no committed binaries.
public sealed class ResumeExtractTests
{
    private const string PdfContentType = "application/pdf";
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Fact]
    public async Task Extract_APdf_ReturnsItsTextAndPageCount()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostExtractAsync(
            client, token, Pdf("Maria Lopez, Senior Engineer", "Experience: BuildCv 2020-2026"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var text = body.RootElement.GetProperty("text").GetString()!;
        text.Should().Contain("Maria Lopez, Senior Engineer");
        text.Should().Contain("Experience: BuildCv 2020-2026");
        body.RootElement.GetProperty("pageCount").GetInt32().Should().Be(2);
        body.RootElement.GetProperty("warnings").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Extract_ADocx_ReturnsItsTextAndNoPageCount()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostExtractAsync(
            client, token, Upload(Docx("Skills: C#, SQL Server"), DocxContentType, "cv.docx"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("text").GetString().Should().Contain("Skills: C#, SQL Server");
        body.RootElement.GetProperty("pageCount").ValueKind.Should().Be(JsonValueKind.Null,
            "a DOCX has no pages until a renderer lays it out");
    }

    [Fact]
    public async Task Extract_PlainText_RoundTrips()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostExtractAsync(
            client, token, Upload(Encoding.UTF8.GetBytes("Plain CV text.\nSecond line."), "text/plain", "cv.txt"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("text").GetString().Should().Be("Plain CV text.\nSecond line.");
        body.RootElement.GetProperty("pageCount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // The brief's central honesty rule, pinned over the wire: a scanned PDF is not a blank document,
    // and the response must say what it actually is.
    [Fact]
    public async Task Extract_AnImageOnlyPdf_WarnsThereIsNoTextLayer()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostExtractAsync(client, token, Pdf("", ""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("pageCount").GetInt32().Should().Be(2);
        body.RootElement.GetProperty("warnings").EnumerateArray().Single().GetString()
            .Should().Contain("no text layer");
    }

    [Fact]
    public async Task Extract_WithAMismatchedDeclaredType_IsRefusedAsProblemDetails()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        // DOCX bytes declared as PDF: the magic bytes decide, not the declaration.
        var response = await PostExtractAsync(
            client, token, Upload(Docx("text"), PdfContentType, "cv.pdf"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Contain("not a PDF");
    }

    [Fact]
    public async Task Extract_WithAnUnsupportedContentType_IsRefusedAsProblemDetails()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostExtractAsync(
            client, token, Upload([1, 2, 3], "image/png", "cv.png"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Contain("Unsupported file type");
    }

    // A multipart body with no `file` part is a binding failure, which ThrowOnBadRequest turns into
    // the ProblemDetails 400 — same class as malformed JSON on the import route.
    [Fact]
    public async Task Extract_WithNoFilePart_IsProblemDetailsShaped()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var content = new MultipartFormDataContent { { new StringContent("value"), "other" } };
        var response = await PostExtractAsync(client, token, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    // A MALFORMED (unterminated) multipart body is the one extract-route error that is NOT
    // ProblemDetails-shaped — a bare 400 with no body and no content type, the second such response in
    // this API beside the 413. This is not an oversight: measured, minimal-API IFormFile binding
    // SWALLOWS the IOException the multipart reader throws ("Unexpected end of Stream") into an empty
    // 400 that never reaches an IExceptionHandler, so MalformedRequestExceptionHandler cannot see it.
    // Shaping it would mean abandoning IFormFile binding for a manual ReadFormAsync, which — measured —
    // also turns the confirmed-solid torn-down 413 into a catchable, shaped one; that is a change to
    // size-enforcement behavior not worth making for a malformed-framing 400. Documented in CLAUDE.md
    // and pinned here so a future framework upgrade that starts shaping it trips this test for review.
    [Fact]
    public async Task Extract_WithAnUnterminatedMultipartBody_IsABare400()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        const string boundary = "BuildCvBoundary";
        // A valid part header and content, but no closing --boundary--: the reader hits end of stream
        // mid-part.
        var body = Encoding.ASCII.GetBytes(
            $"--{boundary}\r\n"
            + "Content-Disposition: form-data; name=\"file\"; filename=\"cv.txt\"\r\n"
            + "Content-Type: text/plain\r\n\r\n"
            + "some content with no terminating boundary");
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", boundary));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes/import/extract")
        {
            Content = content,
        }.WithBearer(token);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("form binding swallows the framing error into a bare 400");
        response.Content.Headers.ContentType.Should().BeNull("no IExceptionHandler runs, so nothing shapes it");
    }

    [Fact]
    public async Task Extract_Unauthenticated_Is401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes/import/extract")
        {
            Content = Upload(Encoding.UTF8.GetBytes("text"), "text/plain", "cv.txt"),
        };

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // The CSRF pair, exactly as pinned for /resumes/import: multipart does not bypass the guard, and
    // the route must never appear in CsrfGuardMiddleware.ExemptPaths. The 200 proves the 403 is about
    // the token, not the request.
    [Fact]
    public async Task Extract_FromACookieClientWithoutTheCsrfToken_IsForbidden()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes/import/extract")
        {
            Content = Upload(Encoding.UTF8.GetBytes("text"), "text/plain", "cv.txt"),
        };

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Extract_FromACookieClientWithTheCsrfToken_IsOk()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var csrfToken = await client.GetAntiforgeryTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes/import/extract")
        {
            Content = Upload(Encoding.UTF8.GetBytes("text"), "text/plain", "cv.txt"),
        };
        request.Headers.Add(CsrfGuardMiddleware.CsrfHeaderName, csrfToken);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Extract_BeyondItsPerAccountCeiling_IsThrottled()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        for (var attempt = 0; attempt < DocumentExtractionRateLimiter.PermitLimit; attempt++)
        {
            (await PostExtractAsync(client, token, Upload(Encoding.UTF8.GetBytes("cv"), "text/plain", "cv.txt")))
                .StatusCode.Should().Be(HttpStatusCode.OK, "attempt {0} is inside the window", attempt);
        }

        var throttled = await PostExtractAsync(
            client, token, Upload(Encoding.UTF8.GetBytes("cv"), "text/plain", "cv.txt"));

        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter.Should().NotBeNull("a throttled client is told when to come back");
    }

    // Discriminates the partition key from the ceiling: a second account on the same address keeps
    // going, which the per-IP window would not allow.
    [Fact]
    public async Task Extract_ByAnotherAccountFromTheSameAddress_IsNotThrottled()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, first) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        for (var attempt = 0; attempt < DocumentExtractionRateLimiter.PermitLimit; attempt++)
            await PostExtractAsync(client, first, Upload(Encoding.UTF8.GetBytes("cv"), "text/plain", "cv.txt"));

        (await PostExtractAsync(client, first, Upload(Encoding.UTF8.GetBytes("cv"), "text/plain", "cv.txt")))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var (_, second) = await client.RegisterAndLoginAsync("second-candidate@example.com");

        (await PostExtractAsync(client, second, Upload(Encoding.UTF8.GetBytes("cv"), "text/plain", "cv.txt")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> PostExtractAsync(
        HttpClient client, string token, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/resumes/import/extract")
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

    private static MultipartFormDataContent Pdf(params string[] pages) =>
        Upload(PdfBytes(pages), PdfContentType, "cv.pdf");

    // One page per string; an empty string is a page with NO text operations at all, which is what a
    // scanned page looks like to a text extractor.
    private static byte[] PdfBytes(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (text.Length > 0)
                page.AddText(text, 12, new PdfPoint(25, 700), font);
        }

        return builder.Build();
    }

    private static byte[] Docx(params string[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                paragraphs.Select(text => new Paragraph(new Run(new Text(text)))).ToArray<OpenXmlElement>()));
        }

        return stream.ToArray();
    }
}

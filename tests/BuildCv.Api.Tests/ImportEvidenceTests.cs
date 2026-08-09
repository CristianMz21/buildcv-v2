using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BuildCv.Api.Tests;

// The whole loop over the real pipeline: upload a document, get a signed token back inside the draft,
// post the draft to the only writer, and ask for a readability report that grades the file.
//
// It is worth having at this layer and not only in the Application tests because the token crosses THREE
// boundaries here — it is minted at the composition root, serialized into a response, deserialized out
// of a request — and a mistake at any of them looks exactly like "the section renormalized out", which
// is a valid answer.
public sealed class ImportEvidenceTests
{
    private const string PdfContentType = "application/pdf";

    // A single-column PDF with real text: the clean upload, and the one the ceiling claim is about.
    [Fact]
    public async Task Propose_ReturnsASignedEvidenceTokenInsideTheDraft()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var body = JsonDocument.Parse(
            await (await ProposeAsync(client, token, OneColumnPdf())).Content.ReadAsStringAsync());

        var evidence = body.RootElement.GetProperty("draft").GetProperty("importEvidence").GetString();
        evidence.Should().NotBeNullOrWhiteSpace();
        evidence.Should().Contain(".", "the token is a payload and a signature");

        // It rides on the DRAFT, which is the object the review screen posts back, and not beside it in
        // confidence — that structure is one-directional by design.
        body.RootElement.GetProperty("confidence").TryGetProperty("importEvidence", out _)
            .Should().BeFalse();
    }

    // THE TRAP, END TO END. A perfect single-column PDF with a text layer must come back with
    // ATS-parseability WEIGHTED and the total at 100 — not renormalized out (which would also read 100
    // and prove nothing) and not capped at 90.
    [Fact]
    public async Task ACleanPdfImportedWithItsEvidence_WeightsTheSectionAndStillScoresOneHundred()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await ImportWithEvidenceAsync(client, token, SingleColumnPdf());

        using var report = JsonDocument.Parse(
            await (await ReadabilityAsync(client, token, resumeId)).Content.ReadAsStringAsync());

        var weights = report.RootElement.GetProperty("breakdown").GetProperty("weights");
        weights.GetProperty("atsParseability").GetDouble().Should().Be(0.10,
            "the evidence arrived, so the section carries its weight instead of being renormalized away");

        SectionScore(report, "AtsParseability").Should().Be(1.0);
        report.RootElement.GetProperty("readabilityScore").GetInt32().Should().Be(100,
            "a cleanly exported CV must reach 100, not 90");
    }

    // The same CV, imported with the token DROPPED. The section renormalizes out and the report still
    // reaches 100 — which is what makes the assertion above evidence of the weight rather than of the
    // total, since the two totals agree and only the weights differ.
    [Fact]
    public async Task TheSameCvImportedWithoutEvidence_RenormalizesTheSectionOutAndStillScoresOneHundred()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await ImportWithEvidenceAsync(client, token, SingleColumnPdf(), sendEvidence: false);

        using var report = JsonDocument.Parse(
            await (await ReadabilityAsync(client, token, resumeId)).Content.ReadAsStringAsync());

        report.RootElement.GetProperty("breakdown").GetProperty("weights")
            .GetProperty("atsParseability").GetDouble().Should().Be(0.0);
        report.RootElement.GetProperty("readabilityScore").GetInt32().Should().Be(100);
    }

    // THE ACCEPTANCE CRITERION over the wire: a two-column upload of the same CV scores lower, and the
    // advice names the fix. Both resumes are imported into the same host from the same text, so the only
    // difference between the two reports is the geometry of the file.
    [Fact]
    public async Task ATwoColumnUploadScoresLowerThanTheSameCvInOneColumn()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var cleanId = await ImportWithEvidenceAsync(client, token, SingleColumnPdf());
        var twoColumnId = await ImportWithEvidenceAsync(client, token, TwoColumnPdf());

        using var clean = JsonDocument.Parse(
            await (await ReadabilityAsync(client, token, cleanId)).Content.ReadAsStringAsync());
        using var twoColumn = JsonDocument.Parse(
            await (await ReadabilityAsync(client, token, twoColumnId)).Content.ReadAsStringAsync());

        SectionScore(twoColumn, "AtsParseability").Should().Be(0.5);
        twoColumn.RootElement.GetProperty("readabilityScore").GetInt32()
            .Should().BeLessThan(clean.RootElement.GetProperty("readabilityScore").GetInt32());

        Kinds(twoColumn).Should().Contain("DocumentUsesMultipleColumns");
        Kinds(clean).Should().NotContain("DocumentUsesMultipleColumns");
    }

    // A TAMPERED TOKEN IS REFUSED, and refused as a FIELD ERROR keyed to the field that carried it —
    // not as a bare 400, which is what a dozen other things about this body would also produce.
    [Fact]
    public async Task AForgedEvidenceToken_IsRejectedAtItsOwnFieldAndCreatesNothing()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var draft = await ProposedDraftAsync(client, token, TwoColumnPdf());
        var forged = Retamper(draft);

        var response = await PostImportAsync(client, token, forged);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").TryGetProperty("importEvidence", out var errors)
            .Should().BeTrue("the client has to be told WHICH field the server refused");
        errors.EnumerateArray().Single().GetString().Should().Contain("not valid");

        // Nothing was created. A 400 that had still written the resume would look identical to this one
        // from the response alone.
        using var list = new HttpRequestMessage(HttpMethod.Get, "/v1/resumes").WithBearer(token);
        using var listed = JsonDocument.Parse(await (await client.SendAsync(list)).Content.ReadAsStringAsync());
        listed.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // THE ACCOUNT BINDING over the wire. The token is minted for one registered candidate and posted by
    // another; everything else about it is genuine.
    [Fact]
    public async Task AnotherAccountsEvidenceToken_IsRejected()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, mine) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, theirs) = await client.RegisterAndLoginAsync("someone.else@example.com");

        var proposed = await ProposedDraftAsync(client, mine, SingleColumnPdf());

        var response = await PostImportAsync(client, theirs, CompleteDraft(EvidenceOf(proposed)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").TryGetProperty("importEvidence", out _)
            .Should().BeTrue();
    }

    // THE GUARANTEE THIS WHOLE DESIGN EXISTS TO PROTECT, restated for the evidence path: minting a token
    // is not a write. ResumeProposeTests.Propose_CreatesNoResume already pins the propose call itself;
    // this pins that ASKING FOR EVIDENCE — the thing that could plausibly have needed a row to hang the
    // signals on — still creates nothing.
    [Fact]
    public async Task MintingEvidence_CreatesNoResume()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await ProposeAsync(client, token, SingleColumnPdf());
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Contain("importEvidence");
        }

        using var list = new HttpRequestMessage(HttpMethod.Get, "/v1/resumes").WithBearer(token);
        using var listed = JsonDocument.Parse(await (await client.SendAsync(list)).Content.ReadAsStringAsync());
        listed.RootElement.GetProperty("items").GetArrayLength()
            .Should().Be(0, "three signed tokens and not one row: the evidence lives in the token");
    }

    private static double SectionScore(JsonDocument report, string section) =>
        report.RootElement.GetProperty("breakdown").GetProperty("sections").EnumerateArray()
            .Single(entry => entry.GetProperty("section").GetString() == section)
            .GetProperty("score").GetDouble();

    private static IEnumerable<string?> Kinds(JsonDocument report) =>
        report.RootElement.GetProperty("recommendations").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString())
            .ToList();

    private static async Task<string> ProposedDraftAsync(
        HttpClient client, string token, MultipartFormDataContent upload)
    {
        var response = await ProposeAsync(client, token, upload);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("draft").GetRawText();
    }

    // The evidence of ONE upload, attached to a COMPLETE, hand-written draft.
    //
    // That split is the realistic flow and not a shortcut: extraction reaches ~65% field accuracy, the
    // parser never invents an experience type, and the review screen is where the candidate fills in
    // what it left blank — so the draft that reaches POST /import is corrected text, not the parser's
    // output. It is also what makes the comparisons below sound: the CV is byte-identical across the
    // clean and two-column cases, so the only thing that can move the score is the file's geometry.
    //
    // It is, incidentally, the replay limit the contract admits: the token describes a document this
    // draft did not come from, and the server accepts it because the binding is to the account.
    private static async Task<Guid> ImportWithEvidenceAsync(
        HttpClient client, string token, MultipartFormDataContent upload, bool sendEvidence = true)
    {
        var proposed = await ProposedDraftAsync(client, token, upload);
        var draft = CompleteDraft(sendEvidence ? EvidenceOf(proposed) : null);

        var response = await PostImportAsync(client, token, draft);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return created.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> PostImportAsync(
        HttpClient client, string token, string draftJson)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = new StringContent(draftJson, Encoding.UTF8, "application/json"),
        }.WithBearer(token);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ReadabilityAsync(HttpClient client, string token, Guid resumeId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/resumes/{resumeId}/readability").WithBearer(token);
        return client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ProposeAsync(
        HttpClient client, string token, MultipartFormDataContent upload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import/propose")
        {
            Content = upload,
        }.WithBearer(token);
        return await client.SendAsync(request);
    }

    private static string EvidenceOf(string proposedDraftJson)
    {
        using var draft = JsonDocument.Parse(proposedDraftJson);
        return draft.RootElement.GetProperty("importEvidence").GetString()!;
    }

    // THE FORGERY A CANDIDATE WOULD ACTUALLY ATTEMPT: the genuine token for their two-column upload with
    // the column-layout byte rewritten to Single, keeping the signature that was minted for the original.
    //
    // It is written at the byte the score reads, and not by scrambling a character at random, because a
    // random scramble lands on the version byte or the account guid and is refused by a guard that has
    // nothing to do with signing — which is what an earlier revision of this test did, and what its
    // negative control caught. Every other check passes here by construction, so only the signature can
    // refuse this body.
    private static string Retamper(string proposedDraftJson)
    {
        var evidence = EvidenceOf(proposedDraftJson);
        var separator = evidence.IndexOf('.', StringComparison.Ordinal);
        var payload = Base64Url.DecodeFromChars(evidence.AsSpan()[..separator]);

        // Offset 25 of the 35-byte payload is ColumnLayout; 1 is Single. Stated as literals on purpose —
        // a test that imported the server's own constants would move with a change to the wire format
        // and stop being an independent statement of it.
        payload[25].Should().Be(2, "the upload this token was minted for is two-column");
        payload[25] = 1;

        return CompleteDraft($"{Base64Url.EncodeToString(payload)}{evidence[separator..]}");
    }

    // A CV with every readability section at its ceiling, so the four sections that are not
    // ATS-parseability all score 1.0 and the total is decided by the fifth alone. It mirrors
    // ReadabilityTestResumes.FullyPopulated on the Application side: two contiguous roles, every bullet
    // point quantified and verb-led, and all three contact channels.
    private static string CompleteDraft(string? importEvidence)
    {
        var evidence = importEvidence is null
            ? string.Empty
            : $",\"importEvidence\":{JsonSerializer.Serialize(importEvidence)}";

        return $$"""
        {
          "contact": {
            "fullName": "Jane Doe",
            "email": "jane.doe@example.com",
            "phoneNumber": "+541155501234",
            "location": "Buenos Aires, Argentina",
            "website": "https://janedoe.dev",
            "summary": "Backend engineer with eight years building payment systems."
          },
          "experiences": [
            {
              "type": "Professional",
              "organization": "Acme",
              "position": "Backend Developer",
              "start": "2019-01-01",
              "end": "2022-01-01",
              "highlights": ["Reduced checkout latency by 40%", "Migrated 12 services to .NET 8"]
            },
            {
              "type": "Professional",
              "organization": "Globex",
              "position": "Senior Backend Developer",
              "start": "2022-01-01",
              "end": "2024-06-01",
              "highlights": ["Reduced incident volume by 30%"]
            }
          ],
          "educations": [
            {
              "institution": "Universidad de Buenos Aires",
              "fieldOfStudy": "Computer Science",
              "start": "2013-03-01",
              "end": "2018-12-01"
            }
          ],
          "skills": [{ "name": "C#" }]{{evidence}}
        }
        """;
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string contentType, string fileName)
    {
        var filePart = new ByteArrayContent(bytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { filePart, "file", fileName } };
    }

    private static MultipartFormDataContent OneColumnPdf() =>
        Upload(OneColumnPdfBytes(["Jane Doe", "jane.doe@example.com"]), PdfContentType, "cv.pdf");

    // Enough lines of real text, in one reading column, for the geometry detector to answer Single and
    // the text-layer check to answer true. What the parser makes of the CONTENT does not matter here:
    // the draft that gets imported is written by hand, so these bytes exist only to be graded.
    private static MultipartFormDataContent SingleColumnPdf() =>
        Upload(OneColumnPdfBytes(CvLines), PdfContentType, "cv.pdf");

    // The same lines with a gutter down the middle, which is the only difference between the two
    // scenarios below and therefore the only thing that can move the ATS-parseability score.
    private static MultipartFormDataContent TwoColumnPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        var baseline = 790d;
        foreach (var line in CvLines[..2])
        {
            page.AddText(line, 12, new PdfPoint(40, baseline), font);
            baseline -= 22;
        }

        var rest = CvLines[2..];
        var half = rest.Length / 2;
        for (var index = 0; index < half; index++)
            page.AddText(rest[index], 12, new PdfPoint(40, baseline - index * 22), font);
        for (var index = half; index < rest.Length; index++)
            page.AddText(rest[index], 12, new PdfPoint(330, baseline - (index - half) * 22), font);

        return Upload(builder.Build(), PdfContentType, "cv.pdf");
    }

    private static readonly string[] CvLines =
    [
        "Jane Doe",
        "jane.doe@example.com",
        "+541155501234",
        "Buenos Aires, Argentina",
        "https://janedoe.dev",
        "SUMMARY",
        "Backend engineer with eight years building payment systems.",
        "EXPERIENCE",
        "Senior Backend Developer at Globex",
        "2022-01-01 - 2024-06-01",
        "Reduced checkout latency by 40%",
        "EDUCATION",
        "Universidad de Buenos Aires",
        "2013-03-01 - 2018-12-01",
        "SKILLS",
        "C#, SQL, Docker",
    ];

    private static byte[] OneColumnPdfBytes(string[] lines)
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

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Infrastructure.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BuildCv.Infrastructure.Tests.Documents;

// The extraction adapters behind IDocumentTextExtractor, exercised through the dispatcher because
// that composition is what production resolves. Every fixture is built in-test — no committed
// binaries.
public sealed class DocumentTextExtractionTests
{
    private const string PdfContentType = "application/pdf";
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static readonly DocumentTextExtractor Extractor =
        new(new PdfPigTextExtractor(), new OpenXmlDocxTextExtractor(), new PlainTextExtractor());

    // ---------------------------------------------------------------- PDF

    [Fact]
    public async Task Pdf_OneColumn_ExtractsTextAndPageCount()
    {
        var result = await Extract(PdfBytes("Maria Lopez\nSenior Engineer"), PdfContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Contain("Maria Lopez");
        result.Value.PageCount.Should().Be(1);
        result.Value.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Pdf_MultiPage_ExtractsEveryPageInOrder()
    {
        var result = await Extract(PdfBytes("First page text", "Second page text", "Third page text"), PdfContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.PageCount.Should().Be(3);
        var text = result.Value.Text;
        text.IndexOf("First page text", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(0);
        text.IndexOf("First page text", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("Second page text", StringComparison.Ordinal));
        text.IndexOf("Second page text", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("Third page text", StringComparison.Ordinal));
    }

    // The brief's honesty rule: an image-only PDF is pages without a text layer, and the answer must
    // say that instead of pretending the document was blank. A page with no text operations is what a
    // scanned page looks like to a text extractor.
    [Fact]
    public async Task Pdf_ImageOnly_SucceedsWithTheNoTextLayerWarning()
    {
        var result = await Extract(PdfBytes("", ""), PdfContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.PageCount.Should().Be(2);
        result.Value.Warnings.Should().ContainSingle()
            .Which.Should().Be(PdfPigTextExtractor.NoTextLayerWarning);
    }

    // A real page of writing sits far above the threshold; the warning must not fire on a short but
    // genuine document.
    [Fact]
    public async Task Pdf_WithLittleButRealText_DoesNotWarn()
    {
        var result = await Extract(PdfBytes("Contact: maria@example.com +34600111222"), PdfContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Pdf_DeclaredButNotAPdf_IsRefusedForTheMagicBytes()
    {
        var result = await Extract(DocxBytes("not a pdf"), PdfContentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not a PDF");
    }

    [Fact]
    public async Task Pdf_WithGarbageAfterTheMagicBytes_IsRefusedAsUnreadable()
    {
        var result = await Extract(Encoding.ASCII.GetBytes("%PDF-1.7 then nothing a parser can use"), PdfContentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The PDF could not be read. It may be corrupt.");
    }

    [Fact]
    public async Task Pdf_WithACancelledToken_PropagatesTheCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => Extractor.ExtractAsync(
            new MemoryStream(PdfBytes("text")), PdfContentType, cancelled.Token);

        // The catch-all must not swallow cancellation into "corrupt": the adapter rethrows it.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // Spanish accents through the PDF path. This is the scenario the original PR could not express: its
    // fixture helper used a Standard-14 font, which in PdfPig's writer throws on 'í' and 'ó' — the exact
    // letters in "María López" — so a Spanish CV PDF was UNTESTABLE, in a Spanish-market product. The
    // fix is an embedded TrueType font (AccentedPdfBytes); the extractor reads accents faithfully from
    // it, as the reviewer independently confirmed.
    //
    // The font comes from the host, located robustly (xUnit 2.9.3 has no runtime skip, and a 350 KB+
    // committed font binary is not worth avoiding a single system dependency). Every environment this
    // runs in has Latin TrueType fonts: ubuntu-latest CI ships DejaVu, and the dev boxes here have it
    // too. A genuinely fontless box fails with an actionable message rather than passing vacuously —
    // which is the project's own rule about controls that never ran.
    [Fact]
    public async Task Pdf_WithSpanishAccents_RoundTripsExactly()
    {
        var fontPath = LocateLatinFont();
        fontPath.Should().NotBeNull(
            "a Latin TrueType font is required to write an accented PDF fixture; install DejaVu, "
            + "Liberation or Noto. CI (ubuntu-latest) and the dev boxes here all have one.");

        var result = await Extract(AccentedPdfBytes(fontPath!, "María López", "ñoño café ürü"), PdfContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Contain("María López");
        result.Value.Text.Should().Contain("ñoño café ürü");
    }

    // ---------------------------------------------------------------- DOCX

    [Fact]
    public async Task Docx_Valid_ExtractsParagraphsAndHasNoPageCount()
    {
        var result = await Extract(DocxBytes("Maria Lopez", "Skills: C#, SQL Server"), DocxContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Be("Maria Lopez\nSkills: C#, SQL Server");
        result.Value.PageCount.Should().BeNull("a DOCX has no pages until a renderer lays it out");
        result.Value.Warnings.Should().BeEmpty();
    }

    // Pins the Descendants<Paragraph>() claim in the adapter: CV layouts love tables, and a paragraph
    // inside a table cell must not be dropped.
    [Fact]
    public async Task Docx_WithTextInsideATable_ExtractsIt()
    {
        var result = await Extract(DocxWithTable("Cell text lives here"), DocxContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Contain("Cell text lives here");
    }

    [Fact]
    public async Task Docx_WithNoText_SucceedsWithTheNoTextWarning()
    {
        var result = await Extract(DocxBytes(), DocxContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().BeEmpty();
        result.Value.Warnings.Should().ContainSingle()
            .Which.Should().Be(OpenXmlDocxTextExtractor.NoTextWarning);
    }

    [Fact]
    public async Task Docx_DeclaredButNotAZip_IsRefusedForTheMagicBytes()
    {
        var result = await Extract(Encoding.UTF8.GetBytes("just text"), DocxContentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not a Word document");
    }

    [Fact]
    public async Task Docx_AZipThatIsNotAnOpcPackage_IsRefused()
    {
        var zip = Zip(("readme.txt", Encoding.UTF8.GetBytes("hello")));

        var result = await Extract(zip, DocxContentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The file is a ZIP archive but not a Word document.");
    }

    // Measured: WordprocessingDocument.Open accepts this package — it is well-formed OPC — and models
    // the absence as a null MainDocumentPart, which is the adapter branch this pins.
    [Fact]
    public async Task Docx_AnOpcPackageWithNoWordDocumentInside_IsRefused()
    {
        var zip = Zip(("[Content_Types].xml", Encoding.UTF8.GetBytes(ContentTypesXml)));

        var result = await Extract(zip, DocxContentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The file is a ZIP archive but not a Word document.");
    }

    [Fact]
    public async Task Docx_ATruncatedArchive_IsRefusedAsUnreadable()
    {
        var truncated = DocxBytes("some text").AsSpan(0, 60).ToArray();

        var result = await Extract(truncated, DocxContentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The Word document could not be read. It may be corrupt.");
    }

    // A 60 KB upload that inflates to 60 MiB, headers telling the truth. The declared-size filter
    // refuses it — and negative control D1 in the PR report shows the read-time counter refuses the
    // same fixture when the filter is deleted, which is why the pair exists.
    [Fact]
    public async Task Docx_AGenuinelyInflatingArchive_IsRefusedForItsDecompressedSize()
    {
        var bomb = InflatingZip(inflatedBytes: 60L * 1024 * 1024);
        bomb.Length.Should().BeLessThan(200 * 1024, "the whole point of a bomb is a small upload");

        var result = await Extract(bomb, DocxContentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(
            $"The document decompresses to more than {OpenXmlDocxTextExtractor.MaxDecompressedBytes / (1024 * 1024)} MB "
            + "and cannot be processed.");
    }

    // The same bomb with MENDACIOUS headers — every uncompressed-size field patched to claim 100
    // bytes, which walks straight past the declared-size filter. MEASURED platform behavior, which
    // the adapter comment relies on: .NET's ZipArchive caps each entry's stream at the size its
    // header declares, so the lie truncates the entry to 100 bytes and OpenXml then refuses the
    // truncated package. The lie buys the attacker a rejection either way; what it must never buy is
    // the inflation.
    [Fact]
    public async Task Docx_ABombWithLyingSizeHeaders_IsRefusedWithoutInflating()
    {
        var bomb = WithLyingUncompressedSizes(InflatingZip(inflatedBytes: 60L * 1024 * 1024), claimedSize: 100);

        // Fixture integrity: the lie must actually be in the bytes, or this test tests nothing.
        using (var archive = new ZipArchive(new MemoryStream(bomb), ZipArchiveMode.Read))
        {
            var entry = archive.GetEntry("word/document.xml")!;
            entry.Length.Should().Be(100, "the patched header claims 100 bytes");
            entry.CompressedLength.Should().BeGreaterThan(10_000,
                "100 true bytes could never need this much compressed data behind them");
        }

        var result = await Extract(bomb, DocxContentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The Word document could not be read. It may be corrupt.");
    }

    // THE AMPLIFICATION ATTACK. A valid OPC package whose document.xml is ~1.5 million one-character
    // paragraphs: ~150 KB on the wire, ~48 MiB decompressed — under BOTH size caps — yet the DOM it
    // asks OpenXml to build is a million-plus objects. Measured before the fix: 662 MiB peak, 200 OK.
    // The streaming reader's text bound refuses it instead. Bytes are not a bound on the work bytes buy;
    // this is the test that was missing when a comment claimed the parse was "ordinary work".
    [Fact]
    public async Task Docx_WithAnAmplifyingElementCount_IsRefusedByTheTextBound()
    {
        var attack = AmplifyingDocx(paragraphs: 1_500_000);
        attack.Length.Should().BeLessThan((int)IDocumentTextExtractor.MaxDocumentBytes,
            "the attack is a small upload — its danger is the inflation, not the wire size");

        using (var archive = new ZipArchive(new MemoryStream(attack), ZipArchiveMode.Read))
        {
            long declared = 0;
            foreach (var entry in archive.Entries)
                declared += entry.Length;
            declared.Should().BeLessThan(OpenXmlDocxTextExtractor.MaxDecompressedBytes,
                "fixture integrity: it must pass the byte caps, or it would be caught by the wrong guard");
        }

        var result = await Extract(attack, DocxContentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("more text than can be processed");
    }

    // The counter-case: a genuinely long CV — 100 paragraphs of 2,000 characters, 200 K in total, an
    // order of magnitude over any real CV and still a fifth of the cap — is ACCEPTED. Pins that the
    // text bound refuses runaways without refusing real content.
    [Fact]
    public async Task Docx_WithALargeButLegalAmountOfText_IsAccepted()
    {
        var paragraph = new string('a', 2_000);
        var large = DocxBytes(Enumerable.Repeat(paragraph, 100).ToArray());

        var result = await Extract(large, DocxContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Length.Should().BeGreaterThan(199_000).And.BeLessThan(OpenXmlDocxTextExtractor.MaxExtractedTextChars);
    }

    // Spanish accents through the DOCX path — the single most product-relevant scenario, and the one
    // the original PR's ASCII-only fixtures never touched. DOCX stores text as UTF-8 XML, so there is
    // no font to embed; the round-trip must be exact.
    [Fact]
    public async Task Docx_WithSpanishAccents_RoundTripsExactly()
    {
        var result = await Extract(DocxBytes("María López", "Ingeniera de software — ñoño, café, ürü"), DocxContentType);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Be("María López\nIngeniera de software — ñoño, café, ürü");
    }

    // The catch-all must not swallow cancellation into "corrupt" on the DOCX path either. Pre-cancelled,
    // the decompression-scan loop's ThrowIfCancellationRequested fires and propagates.
    [Fact]
    public async Task Docx_WithACancelledToken_PropagatesTheCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => Extractor.ExtractAsync(
            new MemoryStream(DocxBytes("text")), DocxContentType, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------- Plain text

    [Fact]
    public async Task PlainText_Utf8_RoundTrips()
    {
        var result = await Extract(Encoding.UTF8.GetBytes("Plain CV text.\nSecond line."), "text/plain");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Be("Plain CV text.\nSecond line.");
        result.Value.PageCount.Should().BeNull();
    }

    // UTF-16 is full of NUL bytes; the byte order mark is what proves they are halves of characters
    // rather than binary garbage.
    [Fact]
    public async Task PlainText_Utf16WithItsByteOrderMark_IsDecoded()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("Texto del CV")).ToArray();

        var result = await Extract(bytes, "text/plain");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Be("Texto del CV");
    }

    [Fact]
    public async Task PlainText_BomlessContentWithANulByte_IsRefusedAsBinary()
    {
        var result = await Extract([(byte)'a', 0x00, (byte)'b'], "text/plain");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The file is not plain text.");
    }

    [Fact]
    public async Task PlainText_DeclaredButActuallyAPdf_IsRefused()
    {
        var result = await Extract(PdfBytes("hidden"), "text/plain");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not plain text");
    }

    [Fact]
    public async Task PlainText_OnlyWhitespace_SucceedsWithTheNoTextWarning()
    {
        var result = await Extract(Encoding.UTF8.GetBytes("  \n\t  "), "text/plain");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().BeEmpty();
        result.Value.Warnings.Should().ContainSingle()
            .Which.Should().Be(PlainTextExtractor.NoTextWarning);
    }

    // Spanish accents through the plain-text path. UTF-8 without a BOM: the í, ó and ñ each carry a
    // high bit but never a NUL byte, so the binary guard lets them through and the StreamReader decodes
    // them.
    [Fact]
    public async Task PlainText_WithSpanishAccents_RoundTripsExactly()
    {
        var result = await Extract(Encoding.UTF8.GetBytes("María López — ñoño, café"), "text/plain");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Be("María López — ñoño, café");
    }

    [Fact]
    public async Task PlainText_WithACancelledToken_PropagatesTheCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => Extractor.ExtractAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("text")), "text/plain", cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------- Dispatch and bounds

    [Fact]
    public async Task ContentTypeParameters_DoNotChangeTheFormat()
    {
        var result = await Extract(Encoding.UTF8.GetBytes("hola"), "text/plain; charset=utf-8");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Be("hola");
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("application/json")]
    [InlineData(null)]
    public async Task UnsupportedOrMissingContentType_IsRefused(string? contentType)
    {
        var result = await Extract(Encoding.UTF8.GetBytes("whatever"), contentType);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Unsupported file type. Upload a PDF, a Word document (.docx) or a plain-text file.");
    }

    // The one wrong declaration worth its own words, because it is the mistake a real candidate
    // makes rather than an attack.
    [Fact]
    public async Task LegacyDoc_IsRefusedWithItsOwnExplanation()
    {
        var result = await Extract(Encoding.UTF8.GetBytes("old format"), "application/msword");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(
            "Legacy .doc files are not supported. Save the document as .docx or PDF and upload it again.");
    }

    [Fact]
    public async Task ASeekableStreamOverTheCeiling_IsRefusedBeforeAnyAdapterRuns()
    {
        using var oversized = new ZeroStream(IDocumentTextExtractor.MaxDocumentBytes + 1, seekable: true);

        var result = await Extractor.ExtractAsync(oversized, "text/plain");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(
            $"The document is larger than {IDocumentTextExtractor.MaxDocumentBytes / (1024 * 1024)} MB.");
    }

    // The port does not get to assume its callers are all HTTP endpoints behind Kestrel's ceiling: a
    // non-seekable stream is buffered through a bounded copy, and the bound is checked as it reads.
    [Fact]
    public async Task ANonSeekableStream_IsBufferedAndExtracted()
    {
        var bytes = Encoding.UTF8.GetBytes("Streamed CV text");
        using var nonSeekable = new NonSeekableStream(bytes);

        var result = await Extractor.ExtractAsync(nonSeekable, "text/plain");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Text.Should().Be("Streamed CV text");
    }

    [Fact]
    public async Task ANonSeekableStreamOverTheCeiling_IsRefusedDuringTheCopy()
    {
        using var oversized = new ZeroStream(IDocumentTextExtractor.MaxDocumentBytes + 1, seekable: false);

        var result = await Extractor.ExtractAsync(oversized, "text/plain");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(
            $"The document is larger than {IDocumentTextExtractor.MaxDocumentBytes / (1024 * 1024)} MB.");
    }

    // ---------------------------------------------------------------- Fixtures

    private static Task<BuildCv.Domain.Common.ValueObjects.Result<DocumentExtraction>> Extract(
        byte[] bytes, string? contentType) =>
        Extractor.ExtractAsync(new MemoryStream(bytes), contentType);

    // One page per string; an empty string is a page with NO text operations, which is what a scanned
    // page looks like to a text extractor.
    private static byte[] PdfBytes(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (text.Length > 0)
            {
                var baseline = 700d;
                foreach (var line in text.Split('\n'))
                {
                    page.AddText(line, 12, new PdfPoint(25, baseline), font);
                    baseline -= 20;
                }
            }
        }

        return builder.Build();
    }

    // The fix for the original PR's structural incapability: a PDF fixture that embeds a real TrueType
    // font, so accented text can be written at all. Standard-14 in PdfPig's writer throws on 'í'/'ó',
    // which is why the ASCII PdfBytes helper above cannot express a Spanish name.
    private static byte[] AccentedPdfBytes(string fontPath, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddTrueTypeFont(File.ReadAllBytes(fontPath));
        foreach (var text in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText(text, 12, new PdfPoint(25, 700), font);
        }

        return builder.Build();
    }

    // A proportional Latin TrueType font with the accented glyphs a Spanish CV needs. Known install
    // paths first (fast), then a recursive search of the standard font roots for a known Latin-complete
    // family — so it finds one wherever fonts live, not only at the exact path a given distro uses.
    private static string? LocateLatinFont()
    {
        string[] knownPaths =
        [
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",       // Debian/Ubuntu (ubuntu-latest CI)
            "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf",     // Fedora
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/usr/share/fonts/liberation-sans-fonts/LiberationSans-Regular.ttf",
            "/Library/Fonts/Arial.ttf",                              // macOS
            "C:\\Windows\\Fonts\\arial.ttf"
        ];
        var direct = knownPaths.FirstOrDefault(File.Exists);
        if (direct is not null)
            return direct;

        // Named families known to carry full Latin coverage — not just any .ttf, which could be an
        // icon or symbol font with no letters at all.
        string[] families =
        [
            "DejaVuSans.ttf", "LiberationSans-Regular.ttf", "LiberationSerif-Regular.ttf",
            "NotoSans-Regular.ttf", "FreeSans.ttf", "Arial.ttf"
        ];
        string[] roots =
        [
            "/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts", "/System/Library/Fonts"
        ];
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var family in families)
            {
                var hit = Directory.EnumerateFiles(root, family, SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null)
                    return hit;
            }

        return null;
    }

    // The amplification fixture: a VALID OPC package whose word/document.xml is one repeated
    // <w:p><w:r><w:t>x</w:t></w:r></w:p>, streamed straight to the entry so building it costs no memory
    // either. The wrapper parts are the minimum WordprocessingDocument.Open accepts.
    private static byte[] AmplifyingDocx(int paragraphs)
    {
        const string contentTypes =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""";
        const string rels =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""";

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string name, string content)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var entryStream = entry.Open();
                entryStream.Write(Encoding.UTF8.GetBytes(content));
            }

            Write("[Content_Types].xml", contentTypes);
            Write("_rels/.rels", rels);

            var document = archive.CreateEntry("word/document.xml", CompressionLevel.SmallestSize);
            using var documentStream = document.Open();
            using var writer = new StreamWriter(documentStream, new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
            for (var i = 0; i < paragraphs; i++)
                writer.Write("<w:p><w:r><w:t>x</w:t></w:r></w:p>");
            writer.Write("</w:body></w:document>");
        }

        return stream.ToArray();
    }

    private static byte[] DocxBytes(params string[] paragraphs)
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

    private static byte[] DocxWithTable(string cellText)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Table(new TableRow(new TableCell(
                new Paragraph(new Run(new Text(cellText))))))));
        }

        return stream.ToArray();
    }

    private const string ContentTypesXml =
        """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>""";

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    // A syntactically fine OPC-looking package whose word/document.xml inflates to `inflatedBytes` of
    // zeros — repeated bytes are what deflate compresses a thousandfold, which is what makes a bomb a
    // small upload.
    private static byte[] InflatingZip(long inflatedBytes)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentTypes = archive.CreateEntry("[Content_Types].xml", CompressionLevel.SmallestSize);
            using (var entryStream = contentTypes.Open())
                entryStream.Write(Encoding.UTF8.GetBytes(ContentTypesXml));

            var payload = archive.CreateEntry("word/document.xml", CompressionLevel.SmallestSize);
            using (var entryStream = payload.Open())
            {
                var chunk = new byte[81920];
                for (long written = 0; written < inflatedBytes; written += chunk.Length)
                    entryStream.Write(chunk);
            }
        }

        return stream.ToArray();
    }

    // Rewrites every uncompressed-size field — offset 22 in each local file header (PK\x03\x04),
    // offset 24 in each central directory header (PK\x01\x02) — to the claimed size, leaving the
    // compressed data and its sizes truthful. This is the "the header is attacker-controlled and can
    // lie" case from the brief, made real.
    private static byte[] WithLyingUncompressedSizes(byte[] zip, uint claimedSize)
    {
        var patched = (byte[])zip.Clone();
        for (var i = 0; i + 30 < patched.Length; i++)
        {
            if (patched[i] != 0x50 || patched[i + 1] != 0x4B)
                continue;

            if (patched[i + 2] == 0x03 && patched[i + 3] == 0x04)
                BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(i + 22, 4), claimedSize);
            else if (patched[i + 2] == 0x01 && patched[i + 3] == 0x02)
                BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(i + 24, 4), claimedSize);
        }

        return patched;
    }

    // A stream of zeros with a chosen length, so an over-ceiling body costs no allocation. The
    // non-seekable flavor is what exercises the bounded copy.
    private sealed class ZeroStream(long length, bool seekable) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => _position = seekable ? value : throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0)
                return 0;

            var toRead = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            seekable ? _position = offset : throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

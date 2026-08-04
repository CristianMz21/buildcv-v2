using System.IO.Compression;
using System.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace BuildCv.Infrastructure.Documents;

/// <summary>
/// DOCX text extraction through the OpenXml SDK, guarded against the two properties that make DOCX its
/// own attack class: it is a ZIP, so the upload ceiling bounds the COMPRESSED size and says nothing
/// about what the archive inflates to; and its decompressed bytes are XML, so a bound on BYTES is not
/// a bound on the WORK those bytes buy — 50 MiB of <c>&lt;w:p&gt;&lt;w:r&gt;&lt;w:t&gt;x&lt;/w:t&gt;</c>
/// is a million-element DOM. Both are bounded here: the byte counter caps ingest, and the streaming
/// reader caps the object graph and the extracted text.
/// </summary>
public sealed class OpenXmlDocxTextExtractor
{
    /// <summary>
    /// The most an uploaded package may decompress to, in total across its entries. 50 MiB is ten
    /// times the 5 MiB upload ceiling: a real CV package holds a few hundred kilobytes of XML plus
    /// media that barely inflates (images are stored already-compressed), so a package inflating past
    /// ten-to-one is a bomb, not a document. This bounds INGEST — how much XML the reader below streams
    /// through — and nothing more; the object graph that XML would build is bounded separately, because
    /// bytes and work are not the same bound (see <see cref="MaxExtractedTextChars"/>).
    /// </summary>
    public const long MaxDecompressedBytes = 50 * 1024 * 1024;

    /// <summary>
    /// The most text one document may yield before it is refused as a runaway. A CV is a few pages;
    /// even a long academic CV rarely clears a few hundred thousand characters, so one million is
    /// several times the largest plausible real CV — while the reviewer's amplification fixture (a
    /// valid package whose <c>document.xml</c> is ~1.5 million one-character paragraphs) produces three
    /// million, so the cap separates the two cleanly. It is the MEMORY bound: the streaming reader
    /// accumulates into a StringBuilder that stops here, which is why one hostile 150 KB upload peaks
    /// at tens of MiB instead of the ~660 MiB the DOM it asked for would have cost. Measured; see
    /// DocumentTextExtractionTests.Docx_WithAnAmplifyingElementCount_IsRefusedByTheTextBound.
    /// </summary>
    public const int MaxExtractedTextChars = 1_000_000;

    public const string NoTextWarning = "The document contains no text.";

    private const string TooMuchTextMessage =
        "The document contains more text than can be processed. If this is a real CV, split it up.";

    private const string NotADocxMessage = "The file is a ZIP archive but not a Word document.";

    public Result<DocumentExtraction> Extract(Stream content, CancellationToken cancellationToken)
    {
        if (!MagicBytes.StartsWith(content, MagicBytes.Zip))
            return Result<DocumentExtraction>.Failure(
                "The file is not a Word document. Check that the upload matches its declared type.");

        try
        {
            using (var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true))
            {
                // Cheap first filter, and KNOWN to be no bound at all: the uncompressed size in a ZIP
                // entry header is written by whoever wrote the file, so a bomb simply lies here. It
                // exists to refuse an honestly-huge file before decompressing anything.
                long declared = 0;
                foreach (var entry in archive.Entries)
                    declared += entry.Length;
                if (declared > MaxDecompressedBytes)
                    return TooLargeDecompressed();

                // Every OPC package carries [Content_Types].xml. A ZIP without it is some other ZIP
                // wearing the extension, and it never reaches the OpenXml SDK at all.
                if (archive.GetEntry("[Content_Types].xml") is null)
                    return Result<DocumentExtraction>.Failure(NotADocxMessage);

                // The read-time bound: inflate every entry and count the bytes it actually yields,
                // refusing once the total passes the cap. Two measured facts sit under this (see the
                // bomb tests in DocumentTextExtractionTests): .NET's ZipArchive additionally CAPS each
                // entry's stream at the size its header declares, so on today's platform an entry that
                // lies small is truncated before this counter could fire and an honest bomb is caught
                // by the filter above — the counter is currently the backstop, not the trigger. But
                // that capping is undocumented behavior of System.IO.Compression, and this loop is
                // what keeps the bound from resting on it: whatever the zip stack yields — and OpenXml
                // reads through the same stack — is counted and capped here before OpenXml parses
                // anything. A genuine document pays one extra decompression, itself bounded by the cap.
                long produced = 0;
                var chunk = new byte[81920];
                foreach (var entry in archive.Entries)
                {
                    using var entryStream = entry.Open();
                    int read;
                    while ((read = entryStream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        // Observed between chunks only; it does not interrupt the read itself.
                        cancellationToken.ThrowIfCancellationRequested();
                        produced += read;
                        if (produced > MaxDecompressedBytes)
                            return TooLargeDecompressed();
                    }
                }
            }

            content.Position = 0;
            using var document = WordprocessingDocument.Open(content, isEditable: false);

            // A zip can be a well-formed OPC package — [Content_Types].xml and all — without being a
            // WORD document. The SDK models that as a missing main part. WordprocessingDocument.Open is
            // lazy — it opens the package without materializing the DOM — so this check is cheap and,
            // crucially, the streaming read below never asks for the DOM either.
            var mainPart = document.MainDocumentPart;
            if (mainPart is null)
                return Result<DocumentExtraction>.Failure(NotADocxMessage);

            var extracted = ExtractBodyText(mainPart, cancellationToken);
            if (extracted is null)
                return Result<DocumentExtraction>.Failure(TooMuchTextMessage);

            IReadOnlyList<string> warnings = extracted.Length == 0 ? [NoTextWarning] : [];
            return Result<DocumentExtraction>.Success(new DocumentExtraction(extracted, PageCount: null, warnings));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Everything, deliberately: a truncated archive, a corrupt deflate stream mid-entry, an
            // OPC part the SDK refuses — all of them are "this upload is unreadable", and none of the
            // libraries' messages are forwarded, for the reason given on PdfPigTextExtractor.
            return Result<DocumentExtraction>.Failure(
                "The Word document could not be read. It may be corrupt.");
        }
    }

    // Streams the main part's XML instead of building its DOM, so a document that would materialize a
    // million-element object graph never does. OpenXmlReader is an XmlReader-backed cursor: it reads
    // one element at a time and holds no more than the current one, so memory tracks the accumulated
    // TEXT, not the element COUNT — which is the whole point, because element count is what a bytes
    // bound cannot see. Returns null when the text passes MaxExtractedTextChars, so the caller can
    // refuse rather than return a partial CV that looks whole.
    //
    // Output is identical to the old string.Join("\n", body.Descendants<Paragraph>().InnerText):
    // measured over a document with multiple runs, empty paragraphs, accents and a table (see the
    // round-trip tests). A newline precedes every paragraph but the first — including paragraphs
    // nested in table cells, which OpenXmlReader visits like any other — and each Text element's
    // content is appended in document order. Body only; headers and footers are separate parts and a
    // CV's content is in the body. No page count — see DocumentExtraction.PageCount.
    private static string? ExtractBodyText(OpenXmlPart mainPart, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var firstParagraph = true;

        using var reader = OpenXmlReader.Create(mainPart);
        while (reader.Read())
        {
            if (!reader.IsStartElement)
                continue;

            // Observed per element only; the byte counter above already bounded how much XML can reach
            // this loop, so this is cooperative cancellation for a long-but-legal document, not the DoS
            // bound (that is the two size caps).
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.ElementType == typeof(Paragraph))
            {
                if (!firstParagraph)
                    text.Append('\n');
                firstParagraph = false;
            }
            else if (reader.ElementType == typeof(Text))
            {
                text.Append(reader.GetText());
                if (text.Length > MaxExtractedTextChars)
                    return null;
            }
        }

        return text.ToString().Trim();
    }

    private static Result<DocumentExtraction> TooLargeDecompressed() =>
        Result<DocumentExtraction>.Failure(
            $"The document decompresses to more than {MaxDecompressedBytes / (1024 * 1024)} MB "
            + "and cannot be processed.");
}

using System.IO.Compression;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace BuildCv.Infrastructure.Documents;

/// <summary>
/// DOCX text extraction through the OpenXml SDK, guarded against the property that makes DOCX its own
/// attack class: it is a ZIP, so the upload ceiling bounds the COMPRESSED size and says nothing about
/// what the archive inflates to.
/// </summary>
public sealed class OpenXmlDocxTextExtractor
{
    /// <summary>
    /// The most an uploaded package may decompress to, in total across its entries. 50 MiB is ten
    /// times the 5 MiB upload ceiling: a real CV package holds a few hundred kilobytes of XML plus
    /// media that barely inflates (images are stored already-compressed), so a package inflating past
    /// ten-to-one is a bomb, not a document. Small enough that the bounded pre-scan below and the
    /// OpenXml parse that follows both stay ordinary amounts of work.
    /// </summary>
    public const long MaxDecompressedBytes = 50 * 1024 * 1024;

    public const string NoTextWarning = "The document contains no text.";

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
            // WORD document. The SDK models that as a missing main part.
            var body = document.MainDocumentPart?.Document?.Body;
            if (body is null)
                return Result<DocumentExtraction>.Failure(NotADocxMessage);

            // Body only: headers, footers and footnotes are separate parts, and a CV's content lives
            // in the body. Descendants<Paragraph>() includes paragraphs nested in tables, which is
            // where CV layouts love to put things. No page count — see DocumentExtraction.PageCount.
            var text = string.Join(
                "\n",
                body.Descendants<Paragraph>().Select(paragraph => paragraph.InnerText)).Trim();

            IReadOnlyList<string> warnings = text.Length == 0 ? [NoTextWarning] : [];
            return Result<DocumentExtraction>.Success(new DocumentExtraction(text, PageCount: null, warnings));
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

    private static Result<DocumentExtraction> TooLargeDecompressed() =>
        Result<DocumentExtraction>.Failure(
            $"The document decompresses to more than {MaxDecompressedBytes / (1024 * 1024)} MB "
            + "and cannot be processed.");
}

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Infrastructure.Documents;

/// <summary>
/// The composition point of <see cref="IDocumentTextExtractor"/>: picks the format adapter by the
/// DECLARED content type, after which the chosen adapter verifies that the bytes actually are that
/// format. The declaration selects, the magic bytes decide.
/// </summary>
public sealed class DocumentTextExtractor(
    PdfPigTextExtractor pdf,
    OpenXmlDocxTextExtractor docx,
    PlainTextExtractor plainText) : IDocumentTextExtractor
{
    public const string PdfContentType = "application/pdf";
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string PlainTextContentType = "text/plain";
    private const string LegacyDocContentType = "application/msword";

    public async Task<Result<DocumentExtraction>> ExtractAsync(
        Stream content, string? declaredContentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // "text/plain; charset=utf-8" declares the same format as "text/plain": parameters modify the
        // media type, they do not change it.
        var mediaType = NormalizeMediaType(declaredContentType);

        // The port enforces its own ceiling instead of trusting that every caller sits behind the
        // endpoint's request-size limit. The seekable branch is the cheap one; the copy that follows is
        // what makes a non-seekable stream both bounded and rewindable, which every adapter needs to
        // sniff magic bytes and then parse from the start.
        if (content.CanSeek && content.Length > IDocumentTextExtractor.MaxDocumentBytes)
            return TooLarge();

        var buffered = content;
        if (!content.CanSeek)
        {
            var buffer = new MemoryStream();
            if (!await TryCopyBoundedAsync(content, buffer, cancellationToken))
                return TooLarge();
            buffer.Position = 0;
            buffered = buffer;
        }

        return mediaType switch
        {
            PdfContentType => pdf.Extract(buffered, cancellationToken),
            DocxContentType => docx.Extract(buffered, cancellationToken),
            PlainTextContentType => plainText.Extract(buffered, cancellationToken),
            // Named specifically because it is the mistake a real candidate makes: the pre-2007 binary
            // .doc format, which none of the adapters read.
            LegacyDocContentType => Result<DocumentExtraction>.Failure(
                "Legacy .doc files are not supported. Save the document as .docx or PDF and upload it again."),
            _ => Result<DocumentExtraction>.Failure(
                "Unsupported file type. Upload a PDF, a Word document (.docx) or a plain-text file.")
        };
    }

    private static Result<DocumentExtraction> TooLarge() =>
        Result<DocumentExtraction>.Failure(
            $"The document is larger than {IDocumentTextExtractor.MaxDocumentBytes / (1024 * 1024)} MB.");

    private static string? NormalizeMediaType(string? declaredContentType)
    {
        if (string.IsNullOrWhiteSpace(declaredContentType))
            return null;

        var separator = declaredContentType.IndexOf(';');
        var mediaType = separator < 0 ? declaredContentType : declaredContentType[..separator];
        return mediaType.Trim().ToLowerInvariant();
    }

    // False once the copy would exceed the ceiling — checked as it reads, so an endless stream costs
    // one buffer over the limit, not the heap.
    private static async Task<bool> TryCopyBoundedAsync(
        Stream source, MemoryStream destination, CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                return true;

            destination.Write(chunk, 0, read);
            if (destination.Length > IDocumentTextExtractor.MaxDocumentBytes)
                return false;
        }
    }
}

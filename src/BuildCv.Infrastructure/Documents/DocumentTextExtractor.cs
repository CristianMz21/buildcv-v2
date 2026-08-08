using System.Diagnostics;
using BuildCv.Application.Common.Observability;
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
    PlainTextExtractor plainText,
    BuildCvMetrics metrics) : IDocumentTextExtractor
{
    public const string PdfContentType = "application/pdf";
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string PlainTextContentType = "text/plain";
    public const string LegacyDocContentType = "application/msword";

    public async Task<Result<DocumentExtraction>> ExtractAsync(
        Stream content, string? declaredContentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // "text/plain; charset=utf-8" declares the same format as "text/plain": parameters modify the
        // media type, they do not change it.
        var mediaType = NormalizeMediaType(declaredContentType);

        // The span wraps the WHOLE dispatch, refusals included, because "how long does an upload take
        // to be refused" is as much a latency question as a successful parse — a decompression bomb is
        // rejected only after the archive has been inflated and counted. StartActivity returns null
        // when no exporter is listening, which is every deployment this build can produce, so the using
        // is a null check rather than a cost.
        //
        // The format tag is DERIVED from the declared type, never the declared type itself: that header
        // is client-controlled, so passing it through would let a caller mint an unbounded number of
        // attribute values. See DocumentFormats.
        using var activity = BuildCvActivities.Source.StartActivity(BuildCvActivities.DocumentExtract);
        activity?.SetTag(BuildCvActivities.DocumentFormatTag, DocumentFormats.Of(mediaType));

        // The port enforces its own ceiling instead of trusting that every caller sits behind the
        // endpoint's request-size limit. The seekable branch is the cheap one; the copy that follows is
        // what makes a non-seekable stream both bounded and rewindable, which every adapter needs to
        // sniff magic bytes and then parse from the start.
        if (content.CanSeek && content.Length > IDocumentTextExtractor.MaxDocumentBytes)
            return Record(activity, TooLarge());

        var buffered = content;
        if (!content.CanSeek)
        {
            var buffer = new MemoryStream();
            if (!await TryCopyBoundedAsync(content, buffer, cancellationToken))
                return Record(activity, TooLarge());
            buffer.Position = 0;
            buffered = buffer;
        }

        return Record(activity, mediaType switch
        {
            PdfContentType => pdf.Extract(buffered, cancellationToken),
            DocxContentType => docx.Extract(buffered, cancellationToken),
            PlainTextContentType => plainText.Extract(buffered, cancellationToken),
            // Named specifically because it is the mistake a real candidate makes: the pre-2007 binary
            // .doc format, which none of the adapters read.
            LegacyDocContentType => Failed(
                DocumentExtractionFailureReasons.LegacyDoc,
                "Legacy .doc files are not supported. Save the document as .docx or PDF and upload it again."),
            _ => Failed(
                DocumentExtractionFailureReasons.UnsupportedType,
                "Unsupported file type. Upload a PDF, a Word document (.docx) or a plain-text file.")
        });
    }

    // Outcome only. The failure MESSAGE never goes on the span: it is written for a candidate, and the
    // one thing a span attribute must never carry is anything derived from the document.
    private static Result<DocumentExtraction> Record(Activity? activity, Result<DocumentExtraction> result)
    {
        activity?.SetTag(
            BuildCvActivities.DocumentOutcomeTag,
            result.IsSuccess ? BuildCvActivities.Extracted : BuildCvActivities.Failed);
        return result;
    }

    // The single place a refusal from THIS class is both counted and worded, so the reason tag and the
    // message cannot describe different refusals. Each adapter has its own copy of this pattern for the
    // same reason; they cannot share one, because a static helper would have no meter to count on.
    private Result<DocumentExtraction> Failed(string reason, string message)
    {
        metrics.ExtractionFailed(reason);
        return Result<DocumentExtraction>.Failure(message);
    }

    private Result<DocumentExtraction> TooLarge() =>
        Failed(
            DocumentExtractionFailureReasons.TooLarge,
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

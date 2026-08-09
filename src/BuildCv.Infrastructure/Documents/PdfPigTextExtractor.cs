using BuildCv.Application.Common.Observability;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace BuildCv.Infrastructure.Documents;

/// <summary>
/// PDF text extraction through PdfPig. Named for its library on purpose, the way
/// <c>AesGcmFieldEncryptor</c> is: PdfPig is pre-1.0, so the day a breaking upgrade lands, this one
/// class is the blast radius.
/// </summary>
public sealed class PdfPigTextExtractor(BuildCvMetrics metrics)
{
    /// <summary>
    /// Below this many non-whitespace characters per page, on average, the document is treated as
    /// having no text layer. A written CV page carries a thousand characters and more; a scanned page
    /// contributes at most a stray artifact like a page number. Averaged over the document rather than
    /// checked per page, so one genuinely written page among scans still counts as text.
    /// </summary>
    public const int NearZeroCharactersPerPage = 5;

    public const string NoTextLayerWarning =
        "The PDF has no text layer — its pages look like scanned images. Extracting the text would "
        + "require OCR, which is not supported; type the details in manually instead.";

    public Result<DocumentExtraction> Extract(Stream content, CancellationToken cancellationToken)
    {
        if (!MagicBytes.StartsWith(content, MagicBytes.Pdf))
            return Failed(
                DocumentExtractionFailureReasons.FormatMismatch,
                "The file is not a PDF. Check that the upload matches its declared type.");

        try
        {
            using var document = PdfDocument.Open(content);

            var pages = new List<string>();
            foreach (var page in document.GetPages())
            {
                // PdfPig accepts no CancellationToken, so this observes cancellation BETWEEN pages
                // only — it does not bound a parse stuck inside one. A hung parse burning its request
                // thread is the accepted cost of the synchronous-parsing ruling; the size ceiling and
                // the per-account throttle are the mitigations that actually bound the exposure.
                cancellationToken.ThrowIfCancellationRequested();
                pages.Add(ContentOrderTextExtractor.GetText(page, addDoubleNewline: true));
            }

            var text = string.Join("\n\n", pages).Trim();

            // An image-only PDF is not a blank document and must not be reported as one: the pages
            // exist, their content is pixels, and the candidate deserves to be told exactly that
            // instead of being shown an empty result that looks like a bug.
            var nonWhitespace = text.Count(character => !char.IsWhiteSpace(character));

            // ONE predicate, two outputs: the sentence the candidate reads and the closed flag that gets
            // signed into the import evidence and scored by the readability engine. Computing them from
            // separate conditions is how a candidate ends up told their PDF is a scan while the score
            // says it parsed fine.
            var hadTextLayer =
                document.NumberOfPages == 0
                || nonWhitespace >= NearZeroCharactersPerPage * document.NumberOfPages;
            IReadOnlyList<string> warnings = hadTextLayer ? [] : [NoTextLayerWarning];

            return Result<DocumentExtraction>.Success(
                new DocumentExtraction(text, document.NumberOfPages, warnings, hadTextLayer));
        }
        catch (PdfDocumentEncryptedException)
        {
            return Failed(
                DocumentExtractionFailureReasons.PasswordProtected,
                "The PDF is password-protected. Remove the password and upload it again.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Deliberately everything: a pre-1.0 parser fed attacker-controlled bytes may throw any
            // type, and every one of them means the same thing here — the document is unreadable.
            // Neither the exception nor its message travels any further: the message is the library's,
            // written about the document's internals, and this adapter does not audit per version
            // whether that quotes candidate text. Refusing to forward any of it is what makes "the CV
            // never reaches a response or a log" hold.
            return Failed(
                DocumentExtractionFailureReasons.Unreadable,
                "The PDF could not be read. It may be corrupt.");
        }
    }

    // Reason and message stated together, so the tag can never describe a different refusal from the
    // one the candidate is shown. The reason is a classification this adapter chose, never anything
    // the parser read out of the file.
    private Result<DocumentExtraction> Failed(string reason, string message)
    {
        metrics.ExtractionFailed(reason);
        return Result<DocumentExtraction>.Failure(message);
    }
}

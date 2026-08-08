using BuildCv.Application.Common.Observability;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Infrastructure.Documents;

/// <summary>
/// Plain-text passthrough. Text has no magic bytes, so this adapter cannot prove the file IS text —
/// it can only refuse the things it can prove text is not.
/// </summary>
public sealed class PlainTextExtractor(BuildCvMetrics metrics)
{
    public const string NoTextWarning = "The document contains no text.";

    public Result<DocumentExtraction> Extract(Stream content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (MagicBytes.StartsWith(content, MagicBytes.Pdf) || MagicBytes.StartsWith(content, MagicBytes.Zip))
            return Failed(
                DocumentExtractionFailureReasons.FormatMismatch,
                "The file is not plain text. It looks like a PDF or an Office document; upload it with "
                + "its real type.");

        // A NUL byte in BOM-less content means binary: UTF-8 never produces one, and the wide
        // encodings that legitimately would — UTF-16 and UTF-32 — announce themselves with the byte
        // order mark tested first, in which case their NUL bytes are expected halves of characters and
        // the StreamReader below decodes them by that mark.
        if (!HasByteOrderMark(content) && ContainsNulByte(content))
            return Failed(DocumentExtractionFailureReasons.BinaryContent, "The file is not plain text.");

        content.Position = 0;
        using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = reader.ReadToEnd().Trim();

        IReadOnlyList<string> warnings = text.Length == 0 ? [NoTextWarning] : [];
        return Result<DocumentExtraction>.Success(new DocumentExtraction(text, PageCount: null, warnings));
    }

    // Reason and message stated together, so the tag can never describe a different refusal from the
    // one the candidate is shown.
    private Result<DocumentExtraction> Failed(string reason, string message)
    {
        metrics.ExtractionFailed(reason);
        return Result<DocumentExtraction>.Failure(message);
    }

    private static bool HasByteOrderMark(Stream content) =>
        MagicBytes.StartsWith(content, [0xEF, 0xBB, 0xBF])     // UTF-8
        || MagicBytes.StartsWith(content, [0xFF, 0xFE])        // UTF-16 LE, and the UTF-32 LE prefix
        || MagicBytes.StartsWith(content, [0xFE, 0xFF]);       // UTF-16 BE

    private static bool ContainsNulByte(Stream content)
    {
        content.Position = 0;
        var chunk = new byte[81920];
        int read;
        while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (Array.IndexOf(chunk, (byte)0, 0, read) >= 0)
                return true;
        }

        content.Position = 0;
        return false;
    }
}

namespace BuildCv.Application.Common.Services;

/// <summary>
/// What a document's column layout looks like to the geometry detector.
/// </summary>
/// <remarks>
/// PR 2's <see cref="IDocumentTextExtractor"/> deliberately returns TEXT and nothing about position,
/// because plain, provable text is what the mechanical extraction can guarantee. Column detection needs
/// word geometry the text has thrown away, which is why it is a separate, PDF-only signal: only a PDF
/// carries bounding boxes to analyse. A pasted-text or DOCX draft has no geometry and is
/// <see cref="Unknown"/> — the parser still runs, it just cannot warn about columns it cannot see.
/// </remarks>
public enum ColumnLayout
{
    // No geometry was available to judge — a non-PDF upload, or a PDF the detector could not read. NOT a
    // claim of single-column; the parser treats it as "cannot tell" and does not raise a column warning.
    Unknown = 0,

    // The words fall in a single reading column. The extracted text is in a trustworthy order.
    Single = 1,

    // A vertical gutter splits the words into two or more columns. This is the dominant failure mode for
    // Spanish-market CVs, and the reason it MATTERS: content-order text extraction can interleave the
    // columns into plausible, wrong prose. The parser must WARN loudly and never present an interleaved
    // read as if it were reliable.
    Multiple = 2
}

/// <summary>
/// Detects whether a PDF is laid out in multiple columns, from the bounding boxes of its words.
/// </summary>
/// <remarks>
/// <para>
/// Returns a plain <see cref="ColumnLayout"/>, not a <see cref="Domain.Common.ValueObjects.Result{T}"/>,
/// on purpose: a failure to read the geometry is NOT a client-facing error the way a corrupt upload is.
/// The text has already been extracted by then; losing the column signal only means the draft is proposed
/// without a column warning, so "could not tell" is a value (<see cref="ColumnLayout.Unknown"/>), not a
/// failure. An implementation swallows its own parse errors into <see cref="ColumnLayout.Unknown"/> rather
/// than throwing.
/// </para>
/// </remarks>
public interface IPdfColumnDetector
{
    Task<ColumnLayout> DetectAsync(Stream pdfContent, CancellationToken cancellationToken = default);
}

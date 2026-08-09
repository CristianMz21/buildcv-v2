namespace BuildCv.Domain.Resumes;

/// <summary>
/// What an uploaded document's column layout looked like to the geometry detector.
/// </summary>
/// <remarks>
/// <para>
/// It lives in the Domain because it is PERSISTED: it is a member of <see cref="ImportSignals"/>, the
/// nullable owned value a resume carries about the document it was imported from, and the readability
/// engine scores it. The detector that produces it is an Application port implemented in Infrastructure;
/// this enum is the vocabulary the two agree on, and there is deliberately only one of it — a second
/// "domain" copy mapped across the boundary would be one fact stated twice.
/// </para>
/// <para>
/// Document text extraction deliberately returns TEXT and nothing about position, because plain,
/// provable text is what the mechanical extraction can guarantee. Column detection needs word geometry
/// the text has thrown away, which is why it is a separate, PDF-only signal: only a PDF carries bounding
/// boxes to analyse. A pasted-text or DOCX draft has no geometry and is <see cref="Unknown"/> — the
/// parser still runs, it just cannot warn about columns it cannot see.
/// </para>
/// <para>
/// Every member states its number because this is persisted as a tinyint. Letting the compiler assign
/// them means inserting a member in the middle silently renumbers every member after it, and every row
/// already on disk starts reading back as a different layout.
/// </para>
/// </remarks>
public enum ColumnLayout
{
    // No geometry was available to judge — a non-PDF upload, or a PDF the detector could not read. NOT a
    // claim of single-column; the parser treats it as "cannot tell" and does not raise a column warning.
    // The readability rule treats it the same way: the column term is dropped from the section rather
    // than scored as a failure, because ignorance is not evidence of a problem.
    Unknown = 0,

    // The words fall in a single reading column. The extracted text is in a trustworthy order.
    Single = 1,

    // A vertical gutter splits the words into two or more columns. This is the dominant failure mode for
    // Spanish-market CVs, and the reason it MATTERS: content-order text extraction can interleave the
    // columns into plausible, wrong prose. The parser must WARN loudly and never present an interleaved
    // read as if it were reliable.
    Multiple = 2
}

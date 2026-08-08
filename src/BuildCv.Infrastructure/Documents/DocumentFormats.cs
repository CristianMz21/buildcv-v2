namespace BuildCv.Infrastructure.Documents;

/// <summary>
/// Every value the <c>buildcv.document.format</c> span attribute may take.
/// </summary>
/// <remarks>
/// This exists because the input it is derived from — the <c>Content-Type</c> a client declares on the
/// upload — is CLIENT-CONTROLLED and unbounded. Putting it on a span verbatim would let a caller mint
/// a new attribute value per request, which is the trace equivalent of unbounded metric cardinality
/// and, since the header is echoed nowhere else, a channel for putting arbitrary text into an
/// exporter. <see cref="Of"/> maps it into this set and everything outside becomes
/// <see cref="Unknown"/>.
/// </remarks>
public static class DocumentFormats
{
    public const string Pdf = "pdf";
    public const string Docx = "docx";
    public const string Text = "text";
    public const string LegacyDoc = "legacy_doc";

    /// <summary>Absent, or a declared type no adapter handles.</summary>
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = [Pdf, Docx, Text, LegacyDoc, Unknown];

    /// <param name="mediaType">
    /// An already-normalized media type — lower-cased, parameters stripped. Normalization lives in
    /// <c>DocumentTextExtractor</c>, which is also what selects the adapter, so the attribute and the
    /// dispatch cannot disagree about what was declared.
    /// </param>
    public static string Of(string? mediaType) => mediaType switch
    {
        DocumentTextExtractor.PdfContentType => Pdf,
        DocumentTextExtractor.DocxContentType => Docx,
        DocumentTextExtractor.PlainTextContentType => Text,
        DocumentTextExtractor.LegacyDocContentType => LegacyDoc,
        _ => Unknown
    };
}

namespace BuildCv.Infrastructure.Documents;

/// <summary>
/// Every value the <c>reason</c> tag on <c>buildcv.documents.extraction_failures</c> may take.
/// </summary>
/// <remarks>
/// <para>
/// A CLASSIFICATION, NOT THE MESSAGE. The refusal messages are prose written for a candidate — some of
/// them interpolate a size, all of them may be rewritten for tone — and putting one in a tag would
/// make a metric series depend on copy-editing. More importantly it would set the precedent that
/// something a user can influence reaches a dimension; nothing the parser read out of the file may
/// ever appear here, because the file is someone's complete CV and a metrics backend is covered by
/// none of this repository's encryption.
/// </para>
/// <para>
/// The reason is named at the site that decides the refusal, beside the message, so the two are one
/// statement rather than two that can drift. <c>DocumentExtractionMetricsTests</c> drives every one of
/// these paths and asserts both directions: nothing outside this set is ever emitted, and every member
/// of it is reachable — so a constant cannot be added to widen an assertion.
/// </para>
/// </remarks>
public static class DocumentExtractionFailureReasons
{
    /// <summary>Over the port's 5 MiB ceiling, seekable or streamed.</summary>
    public const string TooLarge = "too_large";

    /// <summary>The pre-2007 binary <c>.doc</c> format, named separately because real candidates send it.</summary>
    public const string LegacyDoc = "legacy_doc";

    /// <summary>A declared content type none of the adapters handles.</summary>
    public const string UnsupportedType = "unsupported_type";

    /// <summary>The declared type and the leading bytes disagree.</summary>
    public const string FormatMismatch = "format_mismatch";

    /// <summary>Declared as text, and carrying NUL bytes with no byte order mark to explain them.</summary>
    public const string BinaryContent = "binary_content";

    /// <summary>An encrypted PDF.</summary>
    public const string PasswordProtected = "password_protected";

    /// <summary>A ZIP that is a well-formed archive but not a Word document.</summary>
    public const string NotADocx = "not_a_docx";

    /// <summary>A package whose entries inflate past the 50 MiB decompression cap.</summary>
    public const string DecompressionBomb = "decompression_bomb";

    /// <summary>A document yielding more text than the extractor will hold.</summary>
    public const string TooMuchText = "too_much_text";

    /// <summary>
    /// The parser threw. Deliberately ONE bucket: a pre-1.0 parser fed attacker-controlled bytes can
    /// throw any type, and splitting on the exception type would make the tag's cardinality a property
    /// of the library rather than of this code.
    /// </summary>
    public const string Unreadable = "unreadable";

    public static IReadOnlyList<string> All { get; } =
    [
        TooLarge, LegacyDoc, UnsupportedType, FormatMismatch, BinaryContent,
        PasswordProtected, NotADocx, DecompressionBomb, TooMuchText, Unreadable
    ];
}

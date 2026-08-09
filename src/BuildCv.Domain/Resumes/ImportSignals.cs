namespace BuildCv.Domain.Resumes;

/// <summary>
/// What the document a resume was imported from looked like to a parser: everything the ATS-parseability
/// section of the readability engine is allowed to know about it.
/// </summary>
/// <remarks>
/// <para>
/// IT DESCRIBES THE UPLOADED DOCUMENT, NOT THE RESUME. The file is deliberately never persisted, so this
/// is the only trace of it that survives, and it stops being true of the CV the moment the candidate
/// edits a field. That is not a defect to fix later — in a product that never keeps the file,
/// ATS-parseability can only ever be evidence about what was uploaded, and saying so is cheaper than
/// implying otherwise.
/// </para>
/// <para>
/// It is set at construction and never afterwards: <see cref="Resume.Create"/> is the only way in, there
/// is no mutator, and the import use case is the only caller that supplies one. A resume built through
/// any other route carries null, which is what makes "these signals belong to the document this resume
/// came from" a property of the type rather than a convention.
/// </para>
/// <para>
/// EVERY MEMBER IS A CLOSED VALUE — two enums, a bool and a count. Nothing here can carry a fragment of
/// the candidate's document, which is why the whole value is classified plaintext and stays queryable.
/// </para>
/// </remarks>
public sealed record ImportSignals
{
    /// <summary>
    /// What the geometry detector saw. <see cref="Resumes.ColumnLayout.Unknown"/> is "we could not
    /// tell" — a non-PDF upload, or a PDF whose geometry would not read — and is never scored as a
    /// failure.
    /// </summary>
    public ColumnLayout ColumnLayout { get; }

    /// <summary>
    /// Whether the document yielded machine-readable text without OCR. False for a scanned or
    /// photographed PDF, whose pages hold pixels; true for every format that is text-bearing by
    /// construction, where the question does not arise.
    /// </summary>
    public bool HadTextLayer { get; }

    /// <summary>
    /// The page count, when the format states one. Only a PDF does — a DOCX has no pages until a
    /// renderer lays it out, and plain text has none at all — so null means "this format does not know",
    /// not zero. NOTHING SCORES IT: an ATS parses a long PDF exactly as well as a short one, and how much
    /// a candidate should write is advice about content, which Completeness and Achievements already
    /// give. It is carried because it is the one cheap fact about the document's shape, and a signal kept
    /// is a signal a later rule can use; a signal thrown away needs a new migration to get back.
    /// </summary>
    public int? PageCount { get; }

    /// <summary>
    /// Everything else extraction had to say, as a closed bit field. Never the warning strings — see
    /// <see cref="ImportWarningFlags"/>.
    /// </summary>
    public ImportWarningFlags Warnings { get; }

    private ImportSignals(
        ColumnLayout columnLayout, bool hadTextLayer, int? pageCount, ImportWarningFlags warnings)
    {
        ColumnLayout = columnLayout;
        HadTextLayer = hadTextLayer;
        PageCount = pageCount;
        Warnings = warnings;
    }

#pragma warning disable CS8618 // EF Core assigns every mapped member immediately after construction.
    private ImportSignals() { }
#pragma warning restore CS8618

    /// <summary>
    /// Builds a validated set of signals. Every argument comes from either the extraction pipeline or a
    /// token this server signed, so nothing here is a client-facing message — these throw on values the
    /// producers cannot legitimately emit.
    /// </summary>
    public static ImportSignals Create(
        ColumnLayout columnLayout,
        bool hadTextLayer,
        int? pageCount = null,
        ImportWarningFlags warnings = ImportWarningFlags.None)
    {
        // Both enums are persisted as fixed-width columns with unchecked conversions, so an undefined
        // member is durable corrupt data rather than a runtime error — the same failure issue #21 closed
        // on two endpoints. The decode path is the reachable one: a token's bytes are attacker-supplied
        // until the signature is checked, and a caller that verified in the wrong order would arrive here
        // with anything.
        if (!Enum.IsDefined(columnLayout))
            throw new ArgumentOutOfRangeException(
                nameof(columnLayout), columnLayout, "Unknown column layout.");

        // IsDefined is false for any COMBINATION on a [Flags] enum, so it cannot be used here. The mask
        // is every declared bit; anything outside it is a value no producer in this repository emits.
        if ((warnings & ~AllWarnings) != 0)
            throw new ArgumentOutOfRangeException(
                nameof(warnings), warnings, "Unknown import warning flag.");

        // Zero is admitted deliberately. PdfPig reports NumberOfPages and this value reaches the factory
        // from a live upload, so a refusal here would turn a strange-but-readable PDF into a 500 on the
        // propose endpoint; negative is the impossible one and stays refused.
        if (pageCount is < 0)
            throw new ArgumentOutOfRangeException(
                nameof(pageCount), pageCount, "Page count cannot be negative.");

        return new ImportSignals(columnLayout, hadTextLayer, pageCount, warnings);
    }

    private const ImportWarningFlags AllWarnings = ImportWarningFlags.NoTextContent;
}

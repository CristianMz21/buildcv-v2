namespace BuildCv.Domain.Resumes;

/// <summary>
/// The closed set of things extraction had to say about an uploaded document, as a bit field.
/// </summary>
/// <remarks>
/// <para>
/// A [Flags] ENUM RATHER THAN THE WARNING STRINGS, and that is the whole point of the type. The
/// extraction warnings are prose written for a candidate and one of them quotes the document back:
/// the unrecognised-section warning names the heading it could not place, which is a line the candidate
/// wrote. Those strings belong in a response the candidate reads and nowhere near a plaintext analytical
/// column that outlives the file. A closed bit field carries the same facts with nothing in it that came
/// out of the document.
/// </para>
/// <para>
/// It carries only what <see cref="ImportSignals"/> does not already state as a field of its own. There
/// is no <c>NoTextLayer</c> member because <see cref="ImportSignals.HadTextLayer"/> is that fact, and no
/// <c>MultipleColumns</c> member because <see cref="ImportSignals.ColumnLayout"/> is that one — one fact,
/// one place, so the two can never disagree about the same document.
/// </para>
/// <para>
/// Persisted as an int. Adding a member is APPEND-ONLY and each value must be the next power of two:
/// renumbering an existing member rewrites the meaning of every row already written under it.
/// </para>
/// </remarks>
[Flags]
public enum ImportWarningFlags
{
    /// <summary>Extraction had nothing to report beyond the fields on <see cref="ImportSignals"/>.</summary>
    None = 0,

    /// <summary>
    /// The document parsed cleanly and yielded no text at all — an empty DOCX or an empty text file.
    /// Distinct from a scanned PDF, which parses into pages that hold pixels and is reported by
    /// <see cref="ImportSignals.HadTextLayer"/> being false; both mean an ATS reads nothing, and they
    /// are separated because the candidate's fix is different for each.
    /// </summary>
    NoTextContent = 1
}

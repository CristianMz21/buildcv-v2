namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Resumes;

/// <summary>
/// The text of one uploaded document, with the little metadata extraction can honestly provide.
/// </summary>
/// <param name="Text">
/// The extracted text, raw. Never a draft and never a domain object: turning text into sections is a
/// separate, heuristic concern, and this type is the boundary that keeps the mechanical part provable.
/// </param>
/// <param name="PageCount">
/// Only a PDF states its page count, so only a PDF gets one. A DOCX has no pages until a renderer lays
/// it out — the <c>Pages</c> field in its extended properties is optional, written by whichever editor
/// last saved the file, and stale the moment the text changes — and plain text has no pages at all.
/// Null therefore means "this format does not know", not zero.
/// </param>
/// <param name="Warnings">
/// Things the caller should show the candidate that are not failures: a PDF with no text layer, a
/// document that parsed cleanly but contained no text. Empty when there is nothing to say.
/// </param>
/// <param name="HadTextLayer">
/// Whether the document yielded machine-readable text without OCR. False only for a PDF whose pages hold
/// pixels; true for every text-bearing format, where the question does not arise. It is a SECOND,
/// STRUCTURED statement of the warning beside it rather than a substitute, and the two are set together
/// at the one site that decides the refusal — the same rule <c>DocumentExtractionFailureReasons</c>
/// follows, and for the same reason: the string is prose written for a candidate and free to be
/// rewritten, while this is a closed value that gets persisted and scored.
/// </param>
/// <param name="WarningFlags">
/// The closed counterpart of <paramref name="Warnings"/>, carrying only what no other member of this
/// record already states. It is what survives into <see cref="ImportSignals"/>; the strings never do,
/// because one of them quotes a heading out of the candidate's document.
/// </param>
public sealed record DocumentExtraction(
    string Text,
    int? PageCount,
    IReadOnlyList<string> Warnings,
    bool HadTextLayer = true,
    ImportWarningFlags WarningFlags = ImportWarningFlags.None);

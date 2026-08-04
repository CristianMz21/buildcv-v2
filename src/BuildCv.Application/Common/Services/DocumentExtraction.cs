namespace BuildCv.Application.Common.Services;

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
public sealed record DocumentExtraction(
    string Text,
    int? PageCount,
    IReadOnlyList<string> Warnings);

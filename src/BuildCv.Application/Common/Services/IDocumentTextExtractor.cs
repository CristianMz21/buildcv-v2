namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Common.ValueObjects;

/// <summary>
/// Extracts the raw text of an uploaded CV document — PDF, DOCX or plain text.
/// </summary>
/// <remarks>
/// <para>
/// Returns <see cref="Result{T}"/> rather than throwing, because with this port failure is the NORMAL
/// path, not the exceptional one: the bytes are attacker-supplied, and a corrupt file, a lying content
/// type or a decompression bomb are inputs to answer, not bugs to surface. An exception escaping an
/// implementation is therefore a genuine bug and belongs to the 500 handler.
/// </para>
/// <para>
/// Failure messages are written by the implementations in their own words and MUST NOT quote the
/// parsing library's message or any of the document's content: the message reaches the client as
/// ProblemDetails and may be logged, and the document is someone's complete CV.
/// </para>
/// <para>
/// <paramref name="declaredContentType"/> is the client's claim about the format and is verified, not
/// trusted: implementations check the magic bytes and refuse a mismatch.
/// </para>
/// </remarks>
public interface IDocumentTextExtractor
{
    /// <summary>
    /// The largest document this port accepts, and the number the HTTP layer declares as its request
    /// ceiling so the two cannot drift. 5 MiB: a text-bearing CV export is rarely over 1 MB even with a
    /// photo, so this admits every real CV with room to spare while bounding how much work one request
    /// can buy — parsing runs synchronously inside the request by deliberate ruling. It is larger than
    /// the 2 MiB JSON-draft ceiling on POST /resumes/import because a binary document is bulkier than
    /// the draft distilled from it.
    /// </summary>
    public const long MaxDocumentBytes = 5 * 1024 * 1024;

    Task<Result<DocumentExtraction>> ExtractAsync(
        Stream content, string? declaredContentType, CancellationToken cancellationToken = default);
}

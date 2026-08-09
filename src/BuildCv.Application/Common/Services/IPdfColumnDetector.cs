namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Resumes;

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

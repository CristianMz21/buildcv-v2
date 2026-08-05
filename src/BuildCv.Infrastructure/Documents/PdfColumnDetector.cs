using BuildCv.Application.Common.Services;
using UglyToad.PdfPig;

namespace BuildCv.Infrastructure.Documents;

/// <summary>
/// Detects a multi-column PDF layout from the bounding boxes PdfPig exposes for each word. Named for its
/// library like <c>PdfPigTextExtractor</c>: PdfPig is pre-1.0, so if a breaking upgrade lands, the PDF
/// geometry blast radius is this one class.
/// </summary>
/// <remarks>
/// Best-effort by contract (see <see cref="IPdfColumnDetector"/>): the text is already extracted by the
/// time this runs, so a failure to read the geometry is not a client-facing error — it is the absence of
/// a warning, encoded as <see cref="ColumnLayout.Unknown"/>. Every failure path here therefore returns
/// Unknown rather than throwing, EXCEPT cancellation, which propagates like everywhere else.
/// </remarks>
public sealed class PdfColumnDetector : IPdfColumnDetector
{
    public Task<ColumnLayout> DetectAsync(Stream pdfContent, CancellationToken cancellationToken = default)
    {
        try
        {
            if (pdfContent.CanSeek)
                pdfContent.Position = 0;

            var perPage = new List<ColumnLayout>();
            using var document = PdfDocument.Open(pdfContent);
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var words = page.GetWords()
                    .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                    .Select(word => new WordBox(word.BoundingBox.Left, word.BoundingBox.Right))
                    .ToList();

                perPage.Add(ColumnGeometry.DetectPage(words, page.Width));
            }

            return Task.FromResult(ColumnGeometry.Combine(perPage));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Any parser failure means we simply cannot judge the layout — the text still stands, and the
            // draft is proposed without a column warning. Nothing about the document travels further.
            return Task.FromResult(ColumnLayout.Unknown);
        }
    }
}

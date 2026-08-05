namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;

/// <summary>
/// Proposes a best-effort <see cref="ResumeDraft"/> from one uploaded CV document, for a candidate to
/// review and correct before anything reaches the domain.
/// </summary>
/// <remarks>
/// <para>
/// THERE IS NO PATH FROM HERE TO A CREATED RESUME. This handler depends on the two read-only extraction
/// ports and NOTHING that writes — no <c>IResumeRepository</c>, no unit of work. It cannot persist, by
/// construction. The only writer in the whole flow is <c>POST /resumes/import</c>, which takes the draft
/// the candidate confirmed. That separation is the phase's central guarantee: extraction proposes, the
/// candidate commits.
/// </para>
/// <para>
/// A command rather than a query only because it consumes a <see cref="Stream"/> and drives the
/// extractor, exactly as <see cref="ExtractDocumentTextCommand"/> does; it still has no side effect.
/// </para>
/// </remarks>
public sealed record ProposeResumeDraftFromDocumentCommand(
    Stream Content,
    string? ContentType) : ICommand<Result<ResumeDraftProposal>>;

public sealed class ProposeResumeDraftFromDocumentHandler(
    IDocumentTextExtractor extractor,
    IPdfColumnDetector columnDetector)
    : ICommandHandler<ProposeResumeDraftFromDocumentCommand, Result<ResumeDraftProposal>>
{
    public async Task<Result<ResumeDraftProposal>> Handle(
        ProposeResumeDraftFromDocumentCommand command, CancellationToken cancellationToken = default)
    {
        // Buffer once, BOUNDED, because the bytes are read twice: the text extractor consumes the stream,
        // and — for a PDF — the column detector needs the same bytes again for the geometry the text has
        // thrown away. The copy stops at the extractor's own ceiling so a hostile stream cannot blow
        // memory here; an over-ceiling buffer is then refused by the extractor with its canonical message.
        var buffered = await BufferBoundedAsync(command.Content, cancellationToken);
        buffered.Position = 0;

        var extraction = await extractor.ExtractAsync(buffered, command.ContentType, cancellationToken);
        if (!extraction.IsSuccess)
            return Result<ResumeDraftProposal>.Failure(extraction.Error!);

        // Column detection is PDF-only — only a PDF carries word geometry — and best-effort: it swallows
        // its own failures into ColumnLayout.Unknown, so losing the geometry never fails the request.
        var layout = ColumnLayout.Unknown;
        if (IsPdf(command.ContentType))
        {
            buffered.Position = 0;
            layout = await columnDetector.DetectAsync(buffered, cancellationToken);
        }

        var proposal = ResumeTextParser.Parse(
            extraction.Value!.Text, layout, extraction.Value.Warnings);
        return Result<ResumeDraftProposal>.Success(proposal);
    }

    private static async Task<MemoryStream> BufferBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;

            buffer.Write(chunk, 0, read);
            // One buffer past the ceiling is enough for the extractor to see Length > Max and refuse; stop
            // reading so an endless stream costs one chunk over the limit, not the heap.
            if (buffer.Length > IDocumentTextExtractor.MaxDocumentBytes)
                break;
        }

        return buffer;
    }

    private static bool IsPdf(string? contentType) =>
        contentType is not null
        && contentType.Split(';')[0].Trim().Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
}

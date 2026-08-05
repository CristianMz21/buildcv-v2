namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;

/// <summary>
/// Extracts the raw text of one uploaded CV document so the candidate can review and correct it before
/// anything reaches the domain.
/// </summary>
/// <remarks>
/// No <c>RequesterId</c>, unlike its siblings: nothing here is owned, looked up or persisted, so there
/// is no account to authorize against. The endpoint still throttles per account — that is an HTTP
/// concern and lives where the principal does.
/// </remarks>
public sealed record ExtractDocumentTextCommand(
    Stream Content,
    string? ContentType) : ICommand<Result<DocumentExtraction>>;

/// <remarks>
/// No try/catch, for the same reason <see cref="CreateResumeFromDraftHandler"/> has none: the port's
/// contract is that every malformed input — corrupt file, lying content type, decompression bomb —
/// comes back as a failed <see cref="Result{T}"/>, so an exception reaching this handler is a bug, and
/// dressing a bug up as a 400 the client can "fix" is worse than the 500 the exception handler answers.
/// </remarks>
public sealed class ExtractDocumentTextHandler(IDocumentTextExtractor extractor)
    : ICommandHandler<ExtractDocumentTextCommand, Result<DocumentExtraction>>
{
    public Task<Result<DocumentExtraction>> Handle(
        ExtractDocumentTextCommand command, CancellationToken cancellationToken = default) =>
        extractor.ExtractAsync(command.Content, command.ContentType, cancellationToken);
}

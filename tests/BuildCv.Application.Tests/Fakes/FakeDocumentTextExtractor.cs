using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Application.Tests.Fakes;

public sealed class FakeDocumentTextExtractor : IDocumentTextExtractor
{
    public Result<DocumentExtraction> NextResult { get; set; } =
        Result<DocumentExtraction>.Success(new DocumentExtraction(string.Empty, null, []));

    public Stream? LastContent { get; private set; }
    public string? LastContentType { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }
    public int CallCount { get; private set; }

    public Task<Result<DocumentExtraction>> ExtractAsync(
        Stream content, string? declaredContentType, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastContent = content;
        LastContentType = declaredContentType;
        LastCancellationToken = cancellationToken;
        return Task.FromResult(NextResult);
    }
}

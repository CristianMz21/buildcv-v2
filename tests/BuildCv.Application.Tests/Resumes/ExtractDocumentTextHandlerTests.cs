using BuildCv.Application.Common.Services;
using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using FluentAssertions;

namespace BuildCv.Application.Tests.Resumes;

// The handler is deliberately a passthrough — the port returns Result and every expected failure is
// already a failure there — so what these tests pin is exactly that: nothing is added, dropped or
// rewrapped between the endpoint and the port.
public sealed class ExtractDocumentTextHandlerTests
{
    private readonly FakeDocumentTextExtractor _extractor = new();
    private readonly ExtractDocumentTextHandler _handler;

    public ExtractDocumentTextHandlerTests() => _handler = new ExtractDocumentTextHandler(_extractor);

    [Fact]
    public async Task Extract_Success_ReturnsThePortsExtractionUntouched()
    {
        var extraction = new DocumentExtraction("CV text", 3, ["a warning"]);
        _extractor.NextResult = Result<DocumentExtraction>.Success(extraction);
        using var content = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();

        var result = await _handler.Handle(
            new ExtractDocumentTextCommand(content, "application/pdf"), cancellation.Token);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(extraction);
        _extractor.CallCount.Should().Be(1);
        _extractor.LastContent.Should().BeSameAs(content);
        _extractor.LastContentType.Should().Be("application/pdf");
        _extractor.LastCancellationToken.Should().Be(cancellation.Token,
            "cancellation must reach the parser, where it helps at all");
    }

    [Fact]
    public async Task Extract_Failure_PassesThroughUntouched()
    {
        _extractor.NextResult = Result<DocumentExtraction>.Failure("The PDF could not be read. It may be corrupt.");
        using var content = new MemoryStream();

        var result = await _handler.Handle(new ExtractDocumentTextCommand(content, "application/pdf"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The PDF could not be read. It may be corrupt.");
    }
}

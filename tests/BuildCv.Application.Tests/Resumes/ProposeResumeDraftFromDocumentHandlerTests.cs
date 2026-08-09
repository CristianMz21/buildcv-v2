using System.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Resumes;

public sealed class ProposeResumeDraftFromDocumentHandlerTests
{
    private readonly FakeDocumentTextExtractor _extractor = new();
    private readonly FakePdfColumnDetector _columnDetector = new();
    private readonly ProposeResumeDraftFromDocumentHandler _handler;

    public ProposeResumeDraftFromDocumentHandlerTests() =>
        _handler = new ProposeResumeDraftFromDocumentHandler(_extractor, _columnDetector);

    [Fact]
    public async Task Propose_APdf_ParsesTheTextAndConsultsTheColumnGeometry()
    {
        _extractor.NextResult = Result<DocumentExtraction>.Success(
            new DocumentExtraction("Sam Doe\nsam@example.com", 1, []));
        _columnDetector.NextLayout = ColumnLayout.Single;

        var result = await _handler.Handle(Command("application/pdf"));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Draft.Contact!.Email.Should().Be("sam@example.com");
        _columnDetector.CallCount.Should().Be(1, "a PDF carries the geometry the column signal needs");
    }

    [Fact]
    public async Task Propose_ANonPdf_DoesNotAskForColumnGeometry()
    {
        _extractor.NextResult = Result<DocumentExtraction>.Success(
            new DocumentExtraction("Sam Doe\nsam@example.com", null, []));

        var result = await _handler.Handle(Command("text/plain"));

        result.IsSuccess.Should().BeTrue(result.Error);
        _columnDetector.CallCount.Should().Be(0, "only a PDF has word geometry to detect columns from");
        result.Value!.Confidence.Warnings.Should().NotContain(ResumeTextParser.TwoColumnWarning);
    }

    [Fact]
    public async Task Propose_ATwoColumnPdf_CarriesTheWarningFromTheDetector()
    {
        _extractor.NextResult = Result<DocumentExtraction>.Success(
            new DocumentExtraction("Sam Doe\nsam@example.com", 1, []));
        _columnDetector.NextLayout = ColumnLayout.Multiple;

        var result = await _handler.Handle(Command("application/pdf"));

        result.Value!.Confidence.Warnings.Should().Contain(ResumeTextParser.TwoColumnWarning);
        result.Value.Confidence.Overall.Should().Be(OverallConfidence.Low);
    }

    [Fact]
    public async Task Propose_CarriesTheExtractorsOwnWarnings()
    {
        const string noTextLayer = "The PDF has no text layer — its pages look like scanned images.";
        _extractor.NextResult = Result<DocumentExtraction>.Success(
            new DocumentExtraction(string.Empty, 2, [noTextLayer]));

        var result = await _handler.Handle(Command("application/pdf"));

        result.Value!.Confidence.Warnings.Should().Contain(noTextLayer);
    }

    [Fact]
    public async Task Propose_ExtractionFailure_IsReturnedUntouchedAndSkipsGeometry()
    {
        _extractor.NextResult = Result<DocumentExtraction>.Failure("The PDF could not be read. It may be corrupt.");

        var result = await _handler.Handle(Command("application/pdf"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The PDF could not be read. It may be corrupt.");
        _columnDetector.CallCount.Should().Be(0, "there is no point detecting columns in an unreadable file");
    }

    // THE PHASE'S CENTRAL GUARANTEE, at the Application level: there is no path from extraction to a
    // created resume. The handler depends ONLY on the two read-only extraction ports — no repository, no
    // unit of work, nothing that can persist. If anyone wired an "extract and save" convenience by adding
    // a writer here, this goes red. The only writer in the flow is CreateResumeFromDraftHandler behind
    // POST /resumes/import, which takes the draft the candidate confirmed.
    [Fact]
    public void Propose_TheHandler_HasNoDependencyThatCanPersist()
    {
        var constructor = typeof(ProposeResumeDraftFromDocumentHandler).GetConstructors().Single();
        var parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType);

        parameterTypes.Should().BeEquivalentTo(
            [typeof(IDocumentTextExtractor), typeof(IPdfColumnDetector)],
            "extraction reads and proposes; it must not be able to write");
    }

    private static ProposeResumeDraftFromDocumentCommand Command(string contentType) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes("ignored by the fake extractor")), contentType);
}

using System.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Resumes;

// The signals half of the propose handler, in a file of its own so
// ProposeResumeDraftFromDocumentHandlerTests — which owns the no-writer guarantee this whole design was
// built around — keeps its diff to nothing.
//
// What is pinned here is the FOLD: four facts the extraction pipeline already computed and threw away,
// gathered into one closed value with nothing free-text in it.
public class ProposeResumeDraftSignalsTests
{
    private readonly FakeDocumentTextExtractor _extractor = new();
    private readonly FakePdfColumnDetector _columnDetector = new();
    private readonly ProposeResumeDraftFromDocumentHandler _handler;

    public ProposeResumeDraftSignalsTests() =>
        _handler = new ProposeResumeDraftFromDocumentHandler(_extractor, _columnDetector);

    private static ProposeResumeDraftFromDocumentCommand Command(string contentType) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes("ignored by the fake extractor")), contentType);

    // The claim the endpoint's signing step leans on: a proposal this handler returns ALWAYS carries
    // signals, so the null branch at the composition root is unreachable from that route. Asserted over
    // both content types, because the PDF path and the non-PDF path build the layout differently.
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("text/plain")]
    public async Task Propose_EveryProposal_CarriesTheSignalsOfTheDocumentItRead(string contentType)
    {
        _extractor.NextResult = Result<DocumentExtraction>.Success(
            new DocumentExtraction("Sam Doe\nsam@example.com", 1, []));

        var result = await _handler.Handle(Command(contentType));

        result.Value!.Signals.Should().NotBeNull();
    }

    [Fact]
    public async Task Propose_APdf_CarriesTheDetectedLayoutAndThePageCount()
    {
        _extractor.NextResult = Result<DocumentExtraction>.Success(
            new DocumentExtraction("Sam Doe\nsam@example.com", 4, []));
        _columnDetector.NextLayout = ColumnLayout.Multiple;

        var signals = (await _handler.Handle(Command("application/pdf"))).Value!.Signals!;

        signals.ColumnLayout.Should().Be(ColumnLayout.Multiple);
        signals.PageCount.Should().Be(4);
        signals.HadTextLayer.Should().BeTrue();
        signals.Warnings.Should().Be(ImportWarningFlags.None);
    }

    // A non-PDF has no geometry, and Unknown is "we could not tell" rather than a claim of one column.
    // The score reads it that way too — the column term leaves the denominator — so this is the value
    // the whole not-penalised rule rests on.
    [Fact]
    public async Task Propose_ANonPdf_CarriesAnUnknownLayoutAndNoPageCount()
    {
        _extractor.NextResult = Result<DocumentExtraction>.Success(
            new DocumentExtraction("Sam Doe\nsam@example.com", null, []));

        var signals = (await _handler.Handle(Command("text/plain"))).Value!.Signals!;

        signals.ColumnLayout.Should().Be(ColumnLayout.Unknown);
        signals.PageCount.Should().BeNull();
    }

    [Fact]
    public async Task Propose_AScannedPdf_CarriesTheMissingTextLayer()
    {
        _extractor.NextResult = Result<DocumentExtraction>.Success(
            new DocumentExtraction(string.Empty, 2, ["scanned"], HadTextLayer: false));

        var signals = (await _handler.Handle(Command("application/pdf"))).Value!.Signals!;

        signals.HadTextLayer.Should().BeFalse();
    }

    [Fact]
    public async Task Propose_AnEmptyDocument_CarriesTheClosedFlagAndNotTheSentence()
    {
        _extractor.NextResult = Result<DocumentExtraction>.Success(new DocumentExtraction(
            string.Empty, null, ["The document contains no text."],
            WarningFlags: ImportWarningFlags.NoTextContent));

        var signals = (await _handler.Handle(Command("text/plain"))).Value!.Signals!;

        signals.Warnings.Should().Be(ImportWarningFlags.NoTextContent);
    }

    // THE RULE THE WHOLE VALUE OBJECT EXISTS FOR, executed rather than asserted in a comment: an
    // extraction warning that quotes the candidate's document reaches the review screen and stops there.
    // Nothing in the signals is a string, so there is nowhere for it to go — and that is what this
    // checks, by putting a recognisable secret in a warning and reading the signals back.
    [Fact]
    public async Task Propose_TheFreeTextWarnings_NeverReachTheSignals()
    {
        const string quotesTheDocument = "Unrecognised section heading 'MI VIDA PERSONAL Y MI FAMILIA'.";
        _extractor.NextResult = Result<DocumentExtraction>.Success(
            new DocumentExtraction("Sam Doe", 1, [quotesTheDocument]));

        var proposal = (await _handler.Handle(Command("application/pdf"))).Value!;

        proposal.Confidence.Warnings.Should().Contain(quotesTheDocument,
            "the candidate still has to be told; it is the PERSISTED copy that must not exist");
        proposal.Signals!.Warnings.Should().Be(ImportWarningFlags.None);
        proposal.Signals.GetType().GetProperties()
            .Should().NotContain(property => property.PropertyType == typeof(string),
                "a string member is the only way document text could get in here");
    }
}

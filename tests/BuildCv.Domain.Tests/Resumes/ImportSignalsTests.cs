using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Resumes;

// The closed value that carries what an uploaded document looked like to a parser. What is worth pinning
// here is the CLOSEDNESS: both members are persisted into fixed-width columns with unchecked
// conversions, so a value outside either enum is durable corrupt data rather than an error anyone sees —
// the same failure the two endpoint guards in issue #21 closed.
public class ImportSignalsTests
{
    [Fact]
    public void Create_KeepsEveryFieldItWasGiven()
    {
        var signals = ImportSignals.Create(
            ColumnLayout.Multiple, hadTextLayer: true, pageCount: 3, ImportWarningFlags.NoTextContent);

        signals.ColumnLayout.Should().Be(ColumnLayout.Multiple);
        signals.HadTextLayer.Should().BeTrue();
        signals.PageCount.Should().Be(3);
        signals.Warnings.Should().Be(ImportWarningFlags.NoTextContent);
    }

    [Fact]
    public void Create_DefaultsToNoPageCountAndNoWarnings()
    {
        var signals = ImportSignals.Create(ColumnLayout.Unknown, hadTextLayer: true);

        signals.PageCount.Should().BeNull("only a PDF states a page count, and null is not zero");
        signals.Warnings.Should().Be(ImportWarningFlags.None);
    }

    // Reachable from the token decode path, where the bytes are attacker-supplied until the signature
    // verifies — and reachable in the other direction too, because tinyint truncates silently.
    [Theory]
    [InlineData(3)]
    [InlineData(255)]
    [InlineData(-1)]
    public void Create_AnUndefinedColumnLayout_Throws(int layout)
    {
        var act = () => ImportSignals.Create((ColumnLayout)layout, hadTextLayer: true);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*column layout*");
    }

    // Enum.IsDefined answers false for every COMBINATION on a [Flags] enum, so the guard has to be a
    // mask. This pins both halves of that: a declared bit is accepted, an undeclared one is not.
    [Fact]
    public void Create_AnUndeclaredWarningFlag_Throws()
    {
        var act = () => ImportSignals.Create(
            ColumnLayout.Single, hadTextLayer: true, pageCount: null, (ImportWarningFlags)0b1000);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*warning flag*");
    }

    [Fact]
    public void Create_ADeclaredWarningFlag_IsAccepted()
    {
        var act = () => ImportSignals.Create(
            ColumnLayout.Single, hadTextLayer: true, pageCount: null, ImportWarningFlags.NoTextContent);

        act.Should().NotThrow();
    }

    [Fact]
    public void Create_ANegativePageCount_Throws()
    {
        var act = () => ImportSignals.Create(ColumnLayout.Single, hadTextLayer: true, pageCount: -1);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Page count*");
    }

    // Zero is admitted on purpose: it arrives from a live upload through PdfPig's page count, and
    // refusing it would turn a strange-but-readable PDF into a 500 on the propose endpoint.
    [Fact]
    public void Create_AZeroPageCount_IsAccepted()
    {
        var act = () => ImportSignals.Create(ColumnLayout.Single, hadTextLayer: true, pageCount: 0);

        act.Should().NotThrow();
    }

    [Fact]
    public void Resume_Create_WithoutSignals_CarriesNone()
    {
        var resume = Resume.Create(AccountId.New(), Contact());

        resume.ImportSignals.Should().BeNull("a resume built by hand came from no document");
    }

    [Fact]
    public void Resume_Create_WithSignals_CarriesThem()
    {
        var signals = ImportSignals.Create(ColumnLayout.Single, hadTextLayer: true, pageCount: 2);

        Resume.Create(AccountId.New(), Contact(), signals).ImportSignals.Should().BeSameAs(signals);
    }

    // WRITE-ONCE, asserted structurally rather than by trying to write. Resume exposes no mutator for the
    // signals and its setter is private, so the only way in is the factory — which is what makes "these
    // signals belong to the document this resume came from" true of the TYPE rather than of the callers.
    [Fact]
    public void Resume_ExposesNoWayToChangeItsImportSignals()
    {
        var property = typeof(Resume).GetProperty(nameof(Resume.ImportSignals))!;

        property.SetMethod!.IsPublic.Should().BeFalse();

        // The getter is excluded by name rather than by IsSpecialName, so a hand-written
        // SetImportSignals or AttachImportSignals is still caught: those are the shapes a future change
        // would take, and attaching evidence to an existing resume would be evidence about nothing.
        typeof(Resume).GetMethods()
            .Where(method => method.IsPublic
                && method.Name.Contains("ImportSignals", StringComparison.Ordinal)
                && method.Name != $"get_{nameof(Resume.ImportSignals)}")
            .Should().BeEmpty();
    }

    private static ContactInformation Contact() =>
        new(PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
}

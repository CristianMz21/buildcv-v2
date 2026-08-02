using System.Text;
using System.Text.Json;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Persistence.Migrations;
using BuildCv.Infrastructure.Security.Encryption;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BuildCv.Infrastructure.Tests.Persistence;

// A rolled-back column has to be readable by the mapping that will read it, and the column type is
// not what decides that -- the value converter is. `nvarchar(max) NOT NULL DEFAULT ''` is a perfectly
// valid column and an unparseable value.
public class MigrationRollbackTests
{
    // The mapping Analyses.Recommendations had before this chain, constructed rather than described:
    // StringListConverter over JsonListCodec.ToStringList. A build rolled back to that migration is a
    // build running exactly this converter over exactly this default.
    private readonly StringListConverter _preChainMapping = new();

    [Fact]
    public void RollingBack_RestoresARecommendationsDefaultThePreChainMappingCanParse()
    {
        var migration = new AddSectionScoringAndRecommendations();

        var restored = migration.DownOperations
            .OfType<AddColumnOperation>()
            .Should().ContainSingle(operation =>
                operation.Name == "Recommendations" && operation.Schema == "scoring").Subject;

        var scaffoldedDefault = restored.DefaultValue.Should().BeOfType<string>().Subject;

        _preChainMapping.ConvertFromProvider(scaffoldedDefault)
            .Should().BeAssignableTo<IReadOnlyList<string>>()
            .Which.Should().BeEmpty("a rollback leaves rows readable and merely empty of advice");
    }

    // What makes the assertion above a discriminator rather than a restatement of the column type: the
    // default this migration originally scaffolded was the empty string, and the same mapping THROWS on
    // it. Every Analysis row would have failed to load after a rollback -- including every row written
    // before this chain existed, which never held a recommendation in the first place.
    //
    // The `?? []` in JsonListCodec.ToStringList does not cover this. It catches a literal JSON `null`;
    // "" is not JSON at all.
    [Fact]
    public void TheOriginallyScaffoldedDefault_WouldHaveMadeEveryAnalysisRowUnreadable()
    {
        var act = () => _preChainMapping.ConvertFromProvider(string.Empty);

        act.Should().Throw<JsonException>(
            "'' is not JSON, so the rollback would have corrupted rows this chain never wrote");
    }

    // The same defect class, caught on a different migration. EncryptLanguageFluency scaffolded as an
    // AlterColumn, which SQL Server accepts and which leaves every pre-existing fluency in the column
    // as its raw UTF-16 bytes. Dropping the column instead loses those values; ALTERing them keeps
    // bytes the decryptor cannot read, and Fluency is an eagerly-loaded owned property, so that is a
    // resume that no longer loads at all.
    //
    // Language.Fluency ships in InitialCreate and is on main, so this is data that really exists.
    [Fact]
    public void EncryptingFluency_DropsTheColumnRatherThanAlteringPlaintextIntoTheEnvelopeColumn()
    {
        var migration = new EncryptLanguageFluency();

        migration.UpOperations.OfType<AlterColumnOperation>().Should().BeEmpty(
            "an altered nvarchar column keeps bytes that are not an envelope, and the resume then "
            + "fails to load rather than merely losing a line of prose");

        migration.UpOperations.OfType<DropColumnOperation>().Should().ContainSingle(operation =>
            operation.Name == "Fluency" && operation.Schema == "resumes" && operation.Table == "Languages");

        var added = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Should().ContainSingle(operation =>
                operation.Name == "Fluency" && operation.Schema == "resumes" && operation.Table == "Languages")
            .Subject;

        added.ColumnType.Should().Be("varbinary(max)");
        added.DefaultValue.Should().BeNull("a default would be a fixed byte string in every row");

        // The rollback carries the same hazard pointing the other way, so it must be a drop too.
        migration.DownOperations.OfType<AlterColumnOperation>().Should().BeEmpty();
    }

    // What makes the assertions above a discriminator rather than a restatement of the column type:
    // the bytes an AlterColumn would leave behind are REJECTED by the real decryptor. Executed against
    // AesGcmFieldEncryptor rather than reasoned about from the envelope layout.
    //
    // "Bilingüe" is the fixture the language tests already use, and its UTF-16 first byte (0x42) is not
    // the envelope version, so this fails at the very first check — no key, no tag, no AAD involved.
    [Fact]
    public void ThePlaintextAnAlterColumnWouldHaveKept_IsNotDecryptable()
    {
        var carriedOver = Encoding.Unicode.GetBytes("Bilingüe");

        var act = () => PersistenceTestContext.Encryptor().Decrypt(carriedOver, "Language.Fluency");

        act.Should().Throw<FieldDecryptionException>(
            "a resume carrying one of these rows would stop loading entirely, which is worse than "
            + "losing the display text the migration deliberately drops");
    }
}

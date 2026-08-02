using System.Text.Json;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Persistence.Migrations;
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
}

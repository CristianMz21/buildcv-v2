using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Lexicon;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Lexicon;

// THE COUPLING NOTHING IN THE LANGUAGE PROVIDES.
//
// The lexicon is a scoring input: editing SkillLexicon.txt changes what a (resume, posting) pair scores,
// exactly as moving a weight would. Nothing makes ScoringWeightsSnapshot.CurrentSchemaVersion move with
// it — they are a data file in one project and a constant in another — so two analyses could report the
// same version having been scored under two different lexicons. That is the precise failure the version
// exists to prevent, and this test is the only thing standing between the repository and it.
//
// It is in BuildCv.Infrastructure.Tests because that is the one project that can see both.
public class SkillLexiconModelVersionTests
{
    // TO CHANGE THE LEXICON: edit the file, run this test, paste the version it reports below, and bump
    // CurrentSchemaVersion in the same commit. Expect every stored analysis to be re-scored once — that
    // is correct rather than a regression; the stored numbers really were produced by another model.
    //
    // TWO ASSERTIONS, NOT ONE COMPOSITE, so a failure says WHICH half is missing. A single assertion over
    // a tuple would report "something moved" for both the edit-without-bump and the bump-without-edit
    // cases, and they need opposite fixes.
    //
    // THE VERSION MOVES FOR OTHER REASONS TOO, and updating the second pin is then the correct answer
    // rather than a workaround: v4 is the date-precision convention and left the lexicon untouched. The
    // pin still earns its place, because reaching it forces whoever bumped to look at the lexicon and say
    // so. What it must never become is a line someone changes to make a red test green without reading
    // the first assertion — which is exactly why the two are separate.
    [Fact]
    public void TheShippedLexicon_AndTheScoringModelVersion_AreBothPinnedToThisRevision()
    {
        SkillLexicon.Load().Version.Should().Be("baf07c1386ed24ab",
            "the lexicon data changed, so the scoring model changed: bump "
            + "ScoringWeightsSnapshot.CurrentSchemaVersion in the same commit and paste the new version here");

        ScoringWeightsSnapshot.CurrentSchemaVersion.Should().Be(4,
            "the model version must move with the lexicon it consults; this revision of the lexicon has "
            + "been current since v3 and v4 moved the model for the date-precision convention instead");
    }

    // The pin above is only worth having if the version it pins really tracks the data — a constant
    // Version would satisfy it forever. SkillLexiconVersionTests covers that in detail; this is the one
    // line that keeps the two files from being read apart.
    [Fact]
    public void TheVersionBeingPinned_IsDerivedFromTheDataRatherThanFixed()
    {
        SkillLexicon.FromData("C# | csharp").Version
            .Should().NotBe(SkillLexicon.Load().Version);
    }
}

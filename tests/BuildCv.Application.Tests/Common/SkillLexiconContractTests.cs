using System.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Tests.Fakes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Common;

// The three rules ISkillLexicon states, executed against a hand-written implementation.
//
// They are asserted here rather than only against the shipped adapter because they are demands on the
// PORT: a second implementation that satisfied the shipped data's tests and broke one of these would make
// the additive-only matching rule false without a single scoring test noticing.
public class SkillLexiconContractTests
{
    private static readonly ISkillLexicon Lexicon = FakeSkillLexicon.With(
        ("react.js", "React"),
        ("reactjs", "React"),
        ("node.js", "Node.js"));

    [Fact]
    public void Canonicalize_ANullTerm_Throws()
    {
        var act = () => Lexicon.Canonicalize(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // RULE 2, and asserted on the REFERENCE rather than on the value. `Be` would pass for an
    // implementation that trimmed, upper-cased and re-cased its way back to an equal string, and such an
    // implementation would be applying a matching rule the lexicon data never authorised.
    [Fact]
    public void Canonicalize_ATermTheLexiconDoesNotKnow_ReturnsTheVeryInstanceItWasGiven()
    {
        var unknown = new string("Hollerith tabulator".ToCharArray());

        Lexicon.Canonicalize(unknown).Should().BeSameAs(unknown);
    }

    // The same claim over an EMPTY lexicon, which is the configuration the additive-only property rests
    // on: with no entries at all the function is the identity, so comparing two canonical forms is
    // character-for-character the comparison the engine already performed.
    [Theory]
    [InlineData("React")]
    [InlineData("react.js")]
    [InlineData("  padded  ")]
    [InlineData("C#")]
    [InlineData("")]
    public void Canonicalize_UnderAnEmptyLexicon_IsTheIdentity(string term)
    {
        FakeSkillLexicon.Empty.Canonicalize(term).Should().BeSameAs(term);
    }

    [Fact]
    public void Canonicalize_AnAlias_FoldsToItsCanonicalToken()
    {
        Lexicon.Canonicalize("react.js").Should().Be("React");
    }

    // A canonical token is a key of its own. Without this the table's SHAPE would decide the answer:
    // "React" would fall through to the unchanged-term path and still compare equal to "React" today,
    // and would stop doing so the moment a canonical token stopped being the spelling people type.
    [Fact]
    public void Canonicalize_ACanonicalToken_FoldsToItself()
    {
        Lexicon.Canonicalize("React").Should().Be("React");
    }

    [Theory]
    [InlineData("REACT.JS")]
    [InlineData("React.Js")]
    [InlineData("  react.js  ")]
    [InlineData("react.js\t")]
    public void Canonicalize_RecognisesATermRegardlessOfCaseAndSurroundingWhitespace(string term)
    {
        Lexicon.Canonicalize(term).Should().Be("React");
    }

    // RULE 3, and the reason it is not decoration: Technology.Create applies NFC, but Skill.Keywords is a
    // raw string list with no normalization pass at all, so a decomposed keyword can reach a lookup that
    // a composed table key would miss. No alias in the SHIPPED data contains a character whose composed
    // and decomposed forms differ, so the mechanism is exercised here on data that does.
    [Fact]
    public void Canonicalize_RecognisesADecomposedTermThroughAComposedKey()
    {
        var lexicon = FakeSkillLexicon.With(("café", "Café Framework"));
        var decomposed = "café".Normalize(NormalizationForm.FormD);

        decomposed.Should().NotBe("café", "the two forms must really differ or this test proves nothing");
        lexicon.Canonicalize(decomposed).Should().Be("Café Framework");
    }

    // RULE 1. Determinism is not provable by testing, but a lexicon that answered from a mutable cache or
    // a clock fails this on the second call.
    [Fact]
    public void Canonicalize_CalledRepeatedly_AnswersTheSameThingEveryTime()
    {
        var answers = Enumerable.Range(0, 5).Select(_ => Lexicon.Canonicalize("reactjs")).ToList();

        answers.Should().AllBe("React");
    }

    [Fact]
    public void Version_ForTwoLexiconsBuiltFromTheSameEntries_IsTheSame()
    {
        FakeSkillLexicon.With(("react.js", "React")).Version
            .Should().Be(FakeSkillLexicon.With(("react.js", "React")).Version);
    }

    [Fact]
    public void Version_ChangesWhenAnEntryIsAdded()
    {
        FakeSkillLexicon.With(("react.js", "React")).Version
            .Should().NotBe(FakeSkillLexicon.With(("react.js", "React"), ("reactjs", "React")).Version);
    }
}

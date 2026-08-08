using BuildCv.Infrastructure.Lexicon;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Lexicon;

// What Version has to mean for it to be worth having: it names the DATA an answer came from, so it
// changes on every edit that changes an answer and on nothing else.
//
// The lexicon is a SCORING INPUT, so a revision is a scoring model change — the pin tying this version
// to ScoringWeightsSnapshot.CurrentSchemaVersion lives in SkillLexiconModelVersionTests, next to the
// matching rule that made it one.
public class SkillLexiconVersionTests
{
    // Version names the DATA, not the file's bytes. A digest of the raw text would churn on a reflowed
    // comment and would make the pin above a formatting tripwire rather than a model-change one.
    [Fact]
    public void Version_IsUnchangedByCommentsWhitespaceAndLineOrder()
    {
        var one = SkillLexicon.FromData("C# | csharp\nJava | core java");
        var other = SkillLexicon.FromData("# rewritten header\n\n  Java  |  core java  \nC# | csharp\n");

        other.Version.Should().Be(one.Version);
    }

    [Fact]
    public void Version_ChangesWhenAnAliasIsAdded()
    {
        SkillLexicon.FromData("C# | csharp").Version
            .Should().NotBe(SkillLexicon.FromData("C# | csharp | c sharp").Version);
    }

    [Fact]
    public void Version_ChangesWhenAnAliasIsRepointedAtAnotherSkill()
    {
        SkillLexicon.FromData("C# | cs\nF#").Version
            .Should().NotBe(SkillLexicon.FromData("C#\nF# | cs").Version);
    }

    [Fact]
    public void Version_ChangesWhenASkillIsRemoved()
    {
        SkillLexicon.FromData("C# | csharp\nF#").Version
            .Should().NotBe(SkillLexicon.FromData("C# | csharp").Version);
    }

    [Fact]
    public void Version_ForTwoLoadsOfTheSameData_IsTheSame()
    {
        SkillLexicon.Load().Version.Should().Be(SkillLexicon.Load().Version);
    }
}

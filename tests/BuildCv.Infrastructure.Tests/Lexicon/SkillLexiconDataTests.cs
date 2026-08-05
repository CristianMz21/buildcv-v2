using System.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Jobs;
using BuildCv.Infrastructure.Lexicon;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Lexicon;

// What the shipped file says, and that it says it at all. The pairs it must NOT merge live next door in
// SkillLexiconCollisionTests, which is the half that makes this one safe.
public class SkillLexiconDataTests
{
    private static readonly ISkillLexicon Lexicon = SkillLexicon.Load();

    [Fact]
    public void Load_ReadsTheEmbeddedFile_AndParsesEveryEntry()
    {
        // 53, because the file is seeded from JobRequirementExtractor's 53-term vocabulary and the two
        // are meant to stay the same set. Asserted as a number rather than only through the containment
        // test below, which cannot see a term the file DROPPED.
        Lexicon.Should().BeOfType<SkillLexicon>().Which.CanonicalTokens.Should().HaveCount(53);
    }

    // EVERY CANONICAL TOKEN IS A TERM THE EXTRACTOR ALREADY KNOWS, derived from the extractor rather than
    // copied out of it: KnownTechnologies is private, so the check feeds it a document naming each
    // canonical token and asserts it proposes every one back.
    //
    // SCOPED PRECISELY, because it looks stronger than it is: it proves the lexicon adds no skill the job
    // side cannot propose. It does NOT prove the reverse — a term added to KnownTechnologies and not to
    // the file would leave this green. The count above is what catches that, and only because the two
    // numbers agree today.
    [Fact]
    public void EveryCanonicalToken_IsATermTheJobRequirementExtractorRecognises()
    {
        var tokens = SkillLexicon.Load().CanonicalTokens.ToList();

        var proposed = JobRequirementExtractor
            .Extract(string.Join('\n', tokens))
            .Select(requirement => requirement.Skill);

        proposed.Should().BeEquivalentTo(tokens);
    }

    // A canonical token folds to itself, over the whole file. This is what stops a seed term quietly
    // becoming an alias of its neighbour: "SQL" answering "SQL Server" would fail here as well as in the
    // collision suite.
    [Fact]
    public void Canonicalize_EverySeedTerm_AnswersThatTermItself()
    {
        foreach (var token in SkillLexicon.Load().CanonicalTokens)
            Lexicon.Canonicalize(token).Should().Be(token);
    }

    [Theory]
    // The bug this milestone exists to fix, in the three spellings the brief names.
    [InlineData("React.js", "React")]
    [InlineData("ReactJS", "React")]
    [InlineData("react js", "React")]
    [InlineData("C#", "C#")]
    [InlineData("csharp", "C#")]
    [InlineData("c sharp", "C#")]
    [InlineData("c-sharp", "C#")]
    [InlineData("Node.js", "Node.js")]
    [InlineData("NodeJS", "Node.js")]
    [InlineData("node", "Node.js")]
    // Abbreviations, the two-letter ones included — the only two the file admits.
    [InlineData("js", "JavaScript")]
    [InlineData("JS", "JavaScript")]
    [InlineData("ts", "TypeScript")]
    [InlineData("k8s", "Kubernetes")]
    [InlineData("postgres", "PostgreSQL")]
    [InlineData("mongo", "MongoDB")]
    [InlineData("mssql", "SQL Server")]
    [InlineData("microsoft sql server", "SQL Server")]
    // Former names of the same thing.
    [InlineData("dotnet", ".NET")]
    [InlineData(".NET Core", ".NET")]
    [InlineData("ASP.NET Core", "ASP.NET")]
    [InlineData("ruby on rails", "Rails")]
    [InlineData("spring boot", "Spring")]
    [InlineData("amazon web services", "AWS")]
    [InlineData("google cloud platform", "GCP")]
    public void Canonicalize_AKnownSpelling_FoldsToItsSkill(string spelling, string expected)
    {
        Lexicon.Canonicalize(spelling).Should().Be(expected);
    }

    [Theory]
    [InlineData("  React.js  ")]
    [InlineData("REACT.JS")]
    [InlineData("react.JS")]
    public void Canonicalize_RecognisesASpellingRegardlessOfCaseAndPadding(string spelling)
    {
        Lexicon.Canonicalize(spelling).Should().Be("React");
    }

    // Multi-word aliases are written with single spaces in the file; the same collapsing runs on the way
    // in, so a keyword typed with two does not miss. Not decoration: Skill.Keywords is a raw string list
    // that no Domain factory trims or normalizes.
    [Fact]
    public void Canonicalize_CollapsesRepeatedWhitespaceBeforeLookingUp()
    {
        Lexicon.Canonicalize("ms  sql\tserver").Should().Be("SQL Server");
    }

    // NFC, and exercised through FromData rather than through the shipped file because no alias in it
    // contains a character whose composed and decomposed forms differ — asserting it on "React" would
    // prove nothing about normalization. The mechanism matters all the same: Technology.Create composes,
    // Skill.Keywords does not, so a decomposed keyword can reach a composed key.
    [Fact]
    public void Canonicalize_RecognisesADecomposedSpellingThroughAComposedKey()
    {
        var lexicon = SkillLexicon.FromData("Café Framework | café");
        var decomposed = "café".Normalize(NormalizationForm.FormD);

        decomposed.Should().NotBe("café", "the two forms must really differ or this proves nothing");
        lexicon.Canonicalize(decomposed).Should().Be("Café Framework");
    }

    // Rule 2 of the port contract, on the SHIPPED data: an unrecognised term comes back as the instance
    // it went in as. Asserted on the reference, because an equal-but-rebuilt string would mean the
    // adapter is applying some normalization of its own on the way out — which would be a matching rule
    // no line of the lexicon authorised, and would break the additive-only property outright.
    [Fact]
    public void Canonicalize_ATermTheFileDoesNotContain_ReturnsTheVeryInstanceItWasGiven()
    {
        var unknown = new string("Fortran".ToCharArray());

        Lexicon.Canonicalize(unknown).Should().BeSameAs(unknown);
    }

    [Fact]
    public void FromData_IgnoresCommentsAndBlankLines_ButNotTheHashInASkillName()
    {
        var lexicon = SkillLexicon.FromData("# a comment\n\n   \nC# | csharp\n  # an indented comment\n");

        lexicon.CanonicalTokens.Should().Equal("C#");
        lexicon.Canonicalize("csharp").Should().Be("C#");
    }

    // A trailing separator is a typo, and the alternative to failing is mapping the empty string onto a
    // real skill — after which Canonicalize("") answers "C#" and any two blank terms match each other.
    [Fact]
    public void FromData_WithAnEmptyAliasField_RefusesToLoad()
    {
        var act = () => SkillLexicon.FromData("C# | csharp | ");

        act.Should().Throw<InvalidOperationException>().WithMessage("*empty alias*");
    }

    [Fact]
    public void FromData_WithNoCanonicalToken_RefusesToLoad()
    {
        var act = () => SkillLexicon.FromData("| csharp");

        act.Should().Throw<InvalidOperationException>().WithMessage("*no canonical token*");
    }

    // The empty lexicon is not a shape the adapter can ship, but it IS the shape the additive-only
    // argument rests on, so the adapter has to behave as one when given nothing.
    [Fact]
    public void FromData_WithNothingButComments_IsTheIdentity()
    {
        var lexicon = SkillLexicon.FromData("# nothing here\n");
        var term = new string("React.js".ToCharArray());

        lexicon.CanonicalTokens.Should().BeEmpty();
        lexicon.Canonicalize(term).Should().BeSameAs(term);
    }
}

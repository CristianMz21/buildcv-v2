using BuildCv.Application.Jobs;
using BuildCv.Domain.Jobs;
using FluentAssertions;

namespace BuildCv.Application.Tests.Jobs;

// The conservative recogniser. Its whole job is to err toward MISSING a skill rather than INVENTING
// one, so the tests are mostly about what it does NOT propose: a lower-cased name, a name inside another
// word, "Java" out of "JavaScript". A proposal it makes wrongly is a score the candidate cannot see is
// wrong; a proposal it misses, the candidate types.
public class JobRequirementExtractorTests
{
    private static IEnumerable<string> SkillsIn(string text) =>
        JobRequirementExtractor.Extract(text).Select(p => p.Skill);

    [Fact]
    public void Extract_RecognisesKnownTechnologies_InOrderOfAppearance()
    {
        SkillsIn("Requirements: C#, React, PostgreSQL and Docker experience.")
            .Should().Equal("C#", "React", "PostgreSQL", "Docker");
    }

    [Fact]
    public void Extract_EveryProposalIsAGuessedNiceToHave()
    {
        JobRequirementExtractor.Extract("We use C# and it is required and mandatory.")
            .Should().OnlyContain(p => p.Priority == RequirementPriority.NiceToHave && p.PriorityGuessed);
    }

    // The gate the whole PR turns on: extraction never proposes MustHave, so "required" / "mandatory" in
    // the text does not inflate a requirement into the Critical-driving rung.
    [Fact]
    public void Extract_NeverProposesMustHave_EvenWhenTheTextDemandsIt()
    {
        JobRequirementExtractor.Extract("C# is REQUIRED. Docker is imprescindible.")
            .Should().OnlyContain(p => p.Priority == RequirementPriority.NiceToHave);
    }

    [Fact]
    public void Extract_DoesNotSplitJavaScriptIntoJava()
    {
        SkillsIn("Strong JavaScript skills.").Should().Equal("JavaScript");
    }

    [Fact]
    public void Extract_PrefersSqlServerOverBareSql()
    {
        SkillsIn("Experience with SQL Server.").Should().Equal("SQL Server");
    }

    [Fact]
    public void Extract_MatchesBareSqlWhenItIsNotSqlServer()
    {
        SkillsIn("Solid SQL knowledge.").Should().Equal("SQL");
    }

    // Case-sensitive on purpose: a lower-cased name is far more likely to be an ordinary word ("spring",
    // "react") than a technology, and missing it is the safe direction.
    [Fact]
    public void Extract_IsCaseSensitive_AndIgnoresLowerCasedCommonWords()
    {
        SkillsIn("In the spring we react to feedback and go for a run.").Should().BeEmpty();
    }

    [Fact]
    public void Extract_DoesNotMatchInsideAWord()
    {
        SkillsIn("Reactor design, Javadoc comments and a Gitlab mirror.").Should().BeEmpty();
    }

    [Fact]
    public void Extract_DeduplicatesARepeatedSkill()
    {
        SkillsIn("C# here, C# there, C# everywhere.").Should().Equal("C#");
    }

    [Fact]
    public void Extract_RecognisesPunctuatedNames()
    {
        SkillsIn("Our stack is .NET, Node.js and C++.").Should().Equal(".NET", "Node.js", "C++");
    }

    [Fact]
    public void Extract_TextWithNoKnownTechnology_ProposesNothing()
    {
        SkillsIn("We want a great communicator who thrives in a team.").Should().BeEmpty();
    }
}

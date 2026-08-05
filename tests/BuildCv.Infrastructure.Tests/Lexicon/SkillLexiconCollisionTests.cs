using BuildCv.Application.Common.Services;
using BuildCv.Infrastructure.Lexicon;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Lexicon;

// THE TEST THAT MAKES THE LEXICON SAFE TO SHIP.
//
// Canonicalization MERGES. Every other test in this folder asks whether an alias works; this one asks
// what the aliases cost. An entry that folds two genuinely different skills onto one token tells a
// candidate they already meet a requirement they do not — in the section they most needed advice on, and
// with the same Critical priority and exact Impact that made the bug this milestone fixes look
// authoritative. That is strictly worse than the miss it was introduced to fix: a miss leaves advice a
// candidate can act on, a false match removes it.
//
// So the pairs below are not examples. They are the near-collisions in and around the 53-term seed, and
// this file is the reason a careless alias cannot land quietly.
public class SkillLexiconCollisionTests
{
    private static readonly ISkillLexicon Lexicon = SkillLexicon.Load();

    // Read as: these two spellings name DIFFERENT skills, and the lexicon must never say otherwise.
    //
    // A term that is not in the file at all still belongs here (C, Go, GitHub, AngularJS, T-SQL,
    // .NET Framework, React Native). Absence is what keeps them apart today, and the day someone adds one
    // as an alias of its neighbour is exactly the day this has to go red.
    [Theory]
    // The seed's own reason for existing: "JavaScript" must never fragment into "Java".
    [InlineData("Java", "JavaScript")]
    [InlineData("Java", "Java Script")]
    [InlineData("Java", "js")]
    [InlineData("JavaScript", "TypeScript")]
    [InlineData("Java", "Kotlin")]
    // Single letters and punctuation. "C" is not in the file; if it is ever added it must not land here.
    [InlineData("C", "C#")]
    [InlineData("C", "C++")]
    [InlineData("C#", "C++")]
    [InlineData("C#", "F#")]
    // The .NET family. ".NET Core" IS an alias of ".NET" — the same runtime under its former name — and
    // that is precisely why the neighbours below have to be checked rather than assumed.
    [InlineData(".NET", "ASP.NET")]
    [InlineData(".NET", ".NET Framework")]
    [InlineData("ASP.NET", ".NET Core")]
    [InlineData("C#", ".NET")]
    // Frameworks that share a name with something else.
    [InlineData("React", "React Native")]
    [InlineData("React", "Preact")]
    [InlineData("React", "Angular")]
    [InlineData("Angular", "AngularJS")]
    [InlineData("Svelte", "SvelteKit")]
    [InlineData("Vue", "Nuxt")]
    // Four framework names one keystroke apart.
    [InlineData("Next.js", "Node.js")]
    [InlineData("Next.js", "Nest.js")]
    [InlineData("Node.js", "Nest.js")]
    [InlineData("Node.js", "Nodemon")]
    // SQL is the language every one of these speaks. Merging it into any product is the collision that
    // would tell a candidate who wrote "SQL" that they know SQL Server.
    [InlineData("SQL", "SQL Server")]
    [InlineData("SQL", "MySQL")]
    [InlineData("SQL", "PostgreSQL")]
    [InlineData("SQL", "SQLite")]
    [InlineData("SQL", "T-SQL")]
    [InlineData("SQL", "PL/SQL")]
    [InlineData("SQL Server", "SQLite")]
    [InlineData("SQL Server", "MySQL")]
    [InlineData("MySQL", "PostgreSQL")]
    [InlineData("Oracle", "MySQL")]
    [InlineData("Go", "MongoDB")]
    [InlineData("MongoDB", "Mongoose")]
    [InlineData("Redis", "Redux")]
    [InlineData("Cassandra", "Redis")]
    [InlineData("Elasticsearch", "Elastic Beanstalk")]
    // A platform is not a spelling of the tool it hosts.
    [InlineData("Git", "GitHub")]
    [InlineData("Git", "GitLab")]
    // "Ruby on Rails" folds to "Rails", which is right — and must not drag "Ruby" along with it.
    [InlineData("Ruby", "Rails")]
    [InlineData("Ruby", "Ruby on Rails")]
    [InlineData("PHP", "Laravel")]
    [InlineData("Python", "Django")]
    [InlineData("Django", "Flask")]
    [InlineData("Flask", "FastAPI")]
    [InlineData("Swift", "SwiftUI")]
    [InlineData("Blazor", "Razor")]
    [InlineData("Kafka", "RabbitMQ")]
    [InlineData("GraphQL", "gRPC")]
    [InlineData("Docker", "Kubernetes")]
    [InlineData("Terraform", "Ansible")]
    [InlineData("Jenkins", "Jira")]
    [InlineData("AWS", "Azure")]
    [InlineData("AWS", "GCP")]
    [InlineData("Azure", "GCP")]
    public void Canonicalize_ForTwoSkillsThatAreNotTheSameSkill_KeepsThemApart(string one, string other)
    {
        Lexicon.Canonicalize(one).Should().NotBe(Lexicon.Canonicalize(other),
            "'{0}' and '{1}' are different skills, and folding them together would tell a candidate "
            + "who has one that they meet a requirement for the other", one, other);
    }

    // THE MISS THIS FILE ACCEPTS, recorded so it is a decision rather than an oversight. "Go" and
    // "Golang" ARE the same skill and the lexicon does not say so, because neither is in the file:
    // JobRequirementExtractor leaves "Go" out of the vocabulary it scans prose with (a capitalised common
    // word would become an invented requirement), and adding a skill here that the extractor cannot
    // propose buys a match on the candidate side only. A miss costs advice the candidate can still act
    // on, so it is the safe half of the asymmetry — but it is a real gap, and it closes in both places at
    // once or not at all.
    [Fact]
    public void Canonicalize_ForGoAndGolang_DoesNotYetMatchThem_BecauseNeitherIsInTheFile()
    {
        Lexicon.Canonicalize("Go").Should().Be("Go");
        Lexicon.Canonicalize("Golang").Should().Be("Golang");
    }

    // The closed-form version of the theory above: no pair of skills in the file merges, checked over
    // every pair rather than over the ones anybody thought of. It is not a substitute for the list —
    // this cannot see a collision with a term the file does NOT contain (Go, GitHub, AngularJS), which is
    // where the more interesting mistakes live — but it is the half that cannot be forgotten.
    [Fact]
    public void Canonicalize_OverEveryPairOfSkillsInTheFile_MergesNone()
    {
        var tokens = SkillLexicon.Load().CanonicalTokens.ToList();

        var merged =
            from one in tokens
            from other in tokens
            where !string.Equals(one, other, StringComparison.Ordinal)
            where string.Equals(Lexicon.Canonicalize(one), Lexicon.Canonicalize(other), StringComparison.OrdinalIgnoreCase)
            select $"{one} == {other}";

        merged.Should().BeEmpty("no two skills in the file may share a canonical token");
        tokens.Should().HaveCountGreaterThan(1, "an empty or single-entry file would satisfy the above vacuously");
    }

    // The collision the PARSER refuses, which is the other half of the guarantee: the theory above can
    // only fail for a collision somebody wrote down, and this is why an accidental one cannot be created
    // by a last-one-wins overwrite. Both directions, so the load failure is about the repeated key and
    // not about the format.
    [Fact]
    public void FromData_WhenTwoSkillsClaimTheSameSpelling_RefusesToLoadAndNamesBoth()
    {
        var act = () => SkillLexicon.FromData("Java | jvm language\nJavaScript | jvm language");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*jvm language*Java*JavaScript*");
    }

    [Fact]
    public void FromData_WithoutTheRepeatedSpelling_Loads()
    {
        var lexicon = SkillLexicon.FromData("Java | jvm language\nJavaScript | js");

        lexicon.Canonicalize("jvm language").Should().Be("Java");
        lexicon.Canonicalize("js").Should().Be("JavaScript");
    }

    // A canonical token is a key like any other, so this collision is caught by the same rule. Worth its
    // own test because it is the shape a real edit takes: someone adds "SQL" to SQL Server's aliases
    // while "SQL" is still a skill of its own.
    [Fact]
    public void FromData_WhenAnAliasRepeatsAnotherSkillsCanonicalToken_RefusesToLoad()
    {
        var act = () => SkillLexicon.FromData("SQL\nSQL Server | sql");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SQL*");
    }
}

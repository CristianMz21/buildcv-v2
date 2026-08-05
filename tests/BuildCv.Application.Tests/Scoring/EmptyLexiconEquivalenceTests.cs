using BuildCv.Application.Common.Services;
using BuildCv.Application.Scoring;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Scoring;

// THE TEST THAT MAKES THE LEXICON PROVABLY NON-REGRESSIVE RATHER THAN HOPEFULLY SO.
//
// The whole safety argument for consulting a lexicon is one sentence: exact whole-string matching runs
// FIRST and wins, so an entry can only turn a non-match into a match and never the reverse. Two things
// follow, and both are executed here rather than reasoned about:
//
//   1. AN EMPTY LEXICON REPRODUCES THE PREVIOUS BEHAVIOUR BIT FOR BIT. Canonicalize is the identity on
//      an empty table, so the second comparison is character-for-character the first one.
//   2. NO CANDIDATE'S SCORE CAN GO DOWN. A populated lexicon is compared against an empty one over the
//      same inputs, and the score is asserted to be greater or equal — never merely "different".
//
// The FIRST claim is also made a second way, and that way is stronger than anything in this file: every
// pre-existing test in ScoringEngineTests, RecommendationBuilderTests, ActingOnARecommendationTests and
// ScoreResumeHandlerTests now runs against FakeSkillLexicon.Empty with its assertions untouched. The
// diff of the commit that introduced the lexicon is the evidence — only the constructor call moved.
//
// This file exists because that evidence covers the pairs those suites happen to use. The sweep below
// covers every ordered pair of a vocabulary chosen from the shipped lexicon's own aliases and
// near-collisions, so an empty lexicon that leaked ANY aliasing would fail it.
public class EmptyLexiconEquivalenceTests
{
    private static readonly DateOnly ReferenceDate = new(2025, 1, 1);

    // A skill name no vocabulary term and no lexicon entry can equal. It occupies the sites that are not
    // under test so each site is exercised on its own.
    private const string Unrelated = "Zzz Unrelated Placeholder";

    // Deliberately drawn from the shipped lexicon: aliases that DO fold together (React/React.js/ReactJS,
    // C#/csharp, Node.js/node, .NET/dotnet, SQL Server/mssql, Kubernetes/k8s) sitting next to the
    // near-collisions that must not (Java/JavaScript, C/C#, SQL/SQL Server, React/React Native,
    // Go/MongoDB, Git/GitHub, Next.js/Node.js). Against an empty lexicon every one of them must behave
    // exactly as whole-string equality, which is what makes a leak visible here.
    private static readonly string[] Vocabulary =
    [
        "React", "React.js", "ReactJS", "react", "React Native",
        "C#", "csharp", "c sharp", "C", "C++",
        "Java", "JavaScript", "js",
        ".NET", "dotnet", ".NET Core", "ASP.NET",
        "Node.js", "NodeJS", "node", "Next.js",
        "SQL", "SQL Server", "mssql",
        "Go", "MongoDB", "mongo",
        "Kubernetes", "k8s",
        "Git", "GitHub"
    ];

    // The three places ScoringRules.IsSatisfiedBy compares a requirement against the resume. Named rather
    // than folded into one, because "the lexicon was wired into one site and not the other two" is the
    // mistake this milestone is most likely to make.
    private enum ComparisonSite
    {
        SkillName,
        SkillKeyword,
        ProjectTechnology
    }

    // THE PRE-M2 RULE, COPIED VERBATIM off ScoringRules.IsSatisfiedBy as it stood at ccd9631:
    //
    //     s.Name.Name.Equals(requirement.Skill.Name, StringComparison.OrdinalIgnoreCase)
    //
    // Restating it here is what an oracle IS — the point is to compare the new code against the old rule,
    // which requires a second copy of the old rule. It is deliberately not shared with production code,
    // because a shared helper would agree with the implementation whatever the implementation did.
    private static bool WholeStringMatch(string candidate, string required) =>
        candidate.Equals(required, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void AnEmptyLexicon_MatchesExactlyWhatWholeStringEqualityMatched_OnAllThreeSites()
    {
        var disagreements = new List<string>();

        foreach (var site in Enum.GetValues<ComparisonSite>())
        {
            foreach (var candidate in Vocabulary)
            {
                foreach (var required in Vocabulary)
                {
                    var actual = Satisfies(candidate, required, site, FakeSkillLexicon.Empty);
                    if (actual != WholeStringMatch(candidate, required))
                        disagreements.Add($"{site}: '{candidate}' vs '{required}' -> {actual}");
                }
            }
        }

        disagreements.Should().BeEmpty("an empty lexicon must reproduce whole-string matching bit for bit");
    }

    // The sweep above can only prove something if it really ran, and if the two answers it compares are
    // both reachable. A vocabulary that never matched anything would satisfy it with a constant `false`
    // implementation; one that always matched would satisfy it with a constant `true`.
    [Fact]
    public void TheEquivalenceSweep_ExercisesBothAnswersOnEverySite()
    {
        foreach (var site in Enum.GetValues<ComparisonSite>())
        {
            var results = (
                from candidate in Vocabulary
                from required in Vocabulary
                select Satisfies(candidate, required, site, FakeSkillLexicon.Empty)).ToList();

            results.Should().Contain(true, "site {0} must be able to match", site);
            results.Should().Contain(false, "site {0} must be able to miss", site);
        }
    }

    // CLAIM 2, executed: no candidate's score goes down. Over the same vocabulary, the score with a
    // populated lexicon is never below the score without one, on every site.
    //
    // WHY THE SKILLS SECTION IS THE WHOLE STORY. The lexicon is consulted from ScoringRules.IsSatisfiedBy
    // and nowhere else, and IsSatisfiedBy feeds only the MATCHED half of SkillWeights. The weights are
    // renormalized over ApplicableSections(totalWeight, languageRequirementCount), both of which are
    // computed without it — so the six weights are identical under either lexicon and a section score
    // that cannot fall makes a weighted total that cannot fall. Asserted on both anyway, because
    // "identical weights" is a property of today's ApplicableSections rather than of this test.
    [Fact]
    public void APopulatedLexicon_NeverLowersAScore_OnAnyPairOrAnySite()
    {
        var populated = FakeSkillLexicon.With(
            ("react.js", "React"), ("reactjs", "React"),
            ("csharp", "C#"), ("c sharp", "C#"),
            ("dotnet", ".NET"), (".net core", ".NET"),
            ("nodejs", "Node.js"), ("node", "Node.js"),
            ("mssql", "SQL Server"),
            ("k8s", "Kubernetes"),
            ("mongo", "MongoDB"),
            ("js", "JavaScript"));

        foreach (var site in Enum.GetValues<ComparisonSite>())
        {
            foreach (var candidate in Vocabulary)
            {
                foreach (var required in Vocabulary)
                {
                    var without = Score(candidate, required, site, FakeSkillLexicon.Empty);
                    var with = Score(candidate, required, site, populated);

                    with.Breakdown.SkillsScore.Should().BeGreaterThanOrEqualTo(without.Breakdown.SkillsScore,
                        "'{0}' vs '{1}' on {2} must not score worse with a lexicon", candidate, required, site);
                    with.Breakdown.WeightedTotal.Should().BeGreaterThanOrEqualTo(without.Breakdown.WeightedTotal,
                        "'{0}' vs '{1}' on {2} must not score worse overall", candidate, required, site);
                }
            }
        }
    }

    // And that the comparison above is not vacuous: the populated lexicon really does raise some of them.
    [Fact]
    public void APopulatedLexicon_RaisesAtLeastOneScoreOnEverySite()
    {
        var populated = FakeSkillLexicon.With(("react.js", "React"));

        foreach (var site in Enum.GetValues<ComparisonSite>())
        {
            Score("React.js", "React", site, FakeSkillLexicon.Empty).Breakdown.SkillsScore.Should().Be(0.0);
            Score("React.js", "React", site, populated).Breakdown.SkillsScore.Should().Be(1.0);
        }
    }

    private static bool Satisfies(string candidate, string required, ComparisonSite site, ISkillLexicon lexicon) =>
        // One requirement of non-zero weight, so the section score is 1.0 when it is satisfied and 0.0
        // when it is not — read through the engine rather than through ScoringRules, which is internal to
        // BuildCv.Application. Going through the public surface is also what makes this a statement about
        // what a candidate is scored, rather than about a helper.
        Score(candidate, required, site, lexicon).Breakdown.SkillsScore == 1.0;

    private static Domain.Scoring.ScoreResult Score(
        string candidate, string required, ComparisonSite site, ISkillLexicon lexicon)
    {
        var jobPosting = JobPosting.Create(AccountId.New(), "Backend Developer", OrganizationName.Create("Acme"));
        jobPosting.AddRequirement(JobRequirement.Create(Technology.Create(required), RequirementPriority.MustHave));

        return new ScoringEngine(lexicon).Score(BuildResume(candidate, site), jobPosting, ReferenceDate);
    }

    private static Resume BuildResume(string candidate, ComparisonSite site)
    {
        var contact = new ContactInformation(PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
        var resume = Resume.Create(AccountId.New(), contact);

        switch (site)
        {
            case ComparisonSite.SkillName:
                resume.AddSkill(Skill.Create(Technology.Create(candidate)));
                break;

            case ComparisonSite.SkillKeyword:
                // The skill's NAME is the placeholder, so a match here can only have come from the
                // keyword site. Without that, this case would also satisfy the skill-name comparison and
                // could not tell the two apart.
                resume.AddSkill(Skill.Create(Technology.Create(Unrelated)) with { Keywords = [candidate] });
                break;

            case ComparisonSite.ProjectTechnology:
                // No skills at all, for the same reason.
                resume.AddProject(new Project("A project", DateRange.Create(ReferenceDate.AddYears(-1)))
                {
                    Technologies = [Technology.Create(candidate)],
                });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(site), site, "Unknown comparison site.");
        }

        return resume;
    }
}

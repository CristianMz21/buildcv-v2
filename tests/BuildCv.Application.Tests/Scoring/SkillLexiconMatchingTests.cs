using BuildCv.Application.Common.Services;
using BuildCv.Application.Scoring;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Application.Tests.Scoring;

// What the lexicon buys, on each of the three places ScoringRules compares a requirement against a
// resume. Whether the SHIPPED file says the right things is a question about data and is answered in
// BuildCv.Infrastructure.Tests; these tests state their own aliases so they cannot start passing or
// failing because someone edited a file in another project.
//
// ONE TEST PER COMPARISON SITE, and that is the point of the file rather than a completeness ritual. The
// lexicon is consulted through a single shared helper, so wiring it into the skill-name comparison and
// forgetting the keyword and project ones is a one-line mistake that every general scoring test would
// survive. Each test below arranges the resume so that ONLY its own site can produce a match, which is
// what makes removing the lexicon from one site red exactly one of them.
public class SkillLexiconMatchingTests
{
    private static readonly DateOnly ReferenceDate = new(2025, 1, 1);

    private static readonly ISkillLexicon Lexicon = FakeSkillLexicon.With(
        ("react.js", "React"),
        ("reactjs", "React"),
        // Two skills that must stay apart, so the miss below is a statement about the lexicon rather
        // than about a term it has never heard of.
        ("java se", "Java"),
        ("js", "JavaScript"));

    // A skill name no alias and no requirement in this file can equal. It fills the sites not under test.
    private const string Unrelated = "Zzz Unrelated Placeholder";

    // ---- site 1: the skill's own name -------------------------------------------------------------

    [Fact]
    public void Match_ASkillNameThatIsAnAliasOfTheRequirement_SatisfiesIt()
    {
        var resume = ResumeWithSkill("React.js");

        SkillsScoreFor(resume).Should().Be(1.0, "'React.js' is 'React'");
    }

    [Fact]
    public void Match_ASkillNameThatIsAnAliasOfTheRequirement_MissesWithoutTheLexicon()
    {
        var resume = ResumeWithSkill("React.js");

        SkillsScoreFor(resume, FakeSkillLexicon.Empty).Should().Be(0.0,
            "this is the behaviour being changed, and asserting it is what makes the test above evidence");
    }

    // ---- site 2: the keywords beside it -----------------------------------------------------------

    // The skill's NAME is unrelated here, so nothing but the keyword comparison can produce a match.
    //
    // Skill.Keywords has no writer anywhere in src/ today — no request can populate it, as
    // ResumeContractTests already records — so this site is reachable only from a Domain-built aggregate.
    // It is still a live comparison in the scoring rule, and leaving it un-aliased while the other two
    // were would be a difference nobody could explain the day a writer arrives.
    [Fact]
    public void Match_ASkillKeywordThatIsAnAliasOfTheRequirement_SatisfiesIt()
    {
        var resume = ResumeWithSkillKeyword("React.js");

        SkillsScoreFor(resume).Should().Be(1.0, "a keyword names the same skill as the skill it sits on");
    }

    [Fact]
    public void Match_ASkillKeywordThatIsAnAliasOfTheRequirement_MissesWithoutTheLexicon()
    {
        SkillsScoreFor(ResumeWithSkillKeyword("React.js"), FakeSkillLexicon.Empty).Should().Be(0.0);
    }

    // ---- site 3: a project's technologies ----------------------------------------------------------

    // No skills at all, so only the project comparison can match.
    [Fact]
    public void Match_AProjectTechnologyThatIsAnAliasOfTheRequirement_SatisfiesIt()
    {
        var resume = ResumeWithProjectTechnology("React.js");

        SkillsScoreFor(resume).Should().Be(1.0, "a technology used on a project is a skill demonstrated");
    }

    [Fact]
    public void Match_AProjectTechnologyThatIsAnAliasOfTheRequirement_MissesWithoutTheLexicon()
    {
        SkillsScoreFor(ResumeWithProjectTechnology("React.js"), FakeSkillLexicon.Empty).Should().Be(0.0);
    }

    // ---- the shape of the match --------------------------------------------------------------------

    // Canonicalization is symmetric, so the alias works whichever side it lands on. Worth executing: an
    // implementation that canonicalized only the candidate would pass every test above.
    [Fact]
    public void Match_WhenTheREQUIREMENTIsTheAliasAndTheResumeHasTheCanonicalName_SatisfiesIt()
    {
        var resume = ResumeWithSkill("React");

        SkillsScoreFor(resume, Lexicon, required: "React.js").Should().Be(1.0);
    }

    [Fact]
    public void Match_TwoAliasesOfOneSkill_SatisfyEachOther()
    {
        SkillsScoreFor(ResumeWithSkill("ReactJS"), Lexicon, required: "React.js").Should().Be(1.0);
    }

    // A term the lexicon has never heard of is unchanged on both sides, so it behaves exactly as it did.
    [Fact]
    public void Match_ATermTheLexiconDoesNotKnow_StillOnlyMatchesItself()
    {
        SkillsScoreFor(ResumeWithSkill("Fortran"), Lexicon, required: "React").Should().Be(0.0);
        SkillsScoreFor(ResumeWithSkill("Fortran"), Lexicon, required: "fortran").Should().Be(1.0,
            "whole-string equality is still case-insensitive and still runs first");
    }

    // The wiring counterpart of the collision suite: two skills the lexicon knows and keeps apart stay
    // apart through the scoring rule. This asserts the RULE does not merge them; that the shipped file
    // does not is SkillLexiconCollisionTests.
    [Fact]
    public void Match_TwoSkillsTheLexiconKeepsApart_DoNotSatisfyEachOther()
    {
        SkillsScoreFor(ResumeWithSkill("java se"), Lexicon, required: "JavaScript").Should().Be(0.0);
        SkillsScoreFor(ResumeWithSkill("js"), Lexicon, required: "Java").Should().Be(0.0);
    }

    // ---- the exact comparison runs first, and stays first -------------------------------------------

    // THE ONLY TEST THAT CAN SEE THE ORDERING. Deleting the exact comparison from
    // ScoringRules.NamesTheSameSkill and leaving the canonical one reds nothing else in the repository —
    // measured, not assumed — because a lexicon obeying the port contract returns unrecognised terms
    // unchanged and recognises case-insensitively, which makes the second comparison true wherever the
    // first would have been.
    //
    // So the ordering is not what makes a CONFORMING lexicon additive. What it buys is that additivity
    // does not depend on conformance: every match the previous engine made survives ANY implementation.
    // MisbehavingSkillLexicon is one that would otherwise break it — it recognises "React" and not
    // "react", so its canonical forms disagree about two strings that are OrdinalIgnoreCase-equal.
    //
    // Without the first operand this is 0.0, and a candidate who wrote their skill in lower case would
    // have lost a match they had before the lexicon existed.
    [Fact]
    public void Match_WhenTheLexiconDisagreesWithItselfAboutCase_TheExactComparisonStillWins()
    {
        var misbehaving = MisbehavingSkillLexicon.RecognisingOnly("React", "React Canonical");

        misbehaving.Canonicalize("react").Should().Be("react",
            "the double must really break the contract or this test proves nothing");
        misbehaving.Canonicalize("React").Should().Be("React Canonical");

        SkillsScoreFor(ResumeWithSkill("react"), misbehaving, required: "React").Should().Be(1.0,
            "whole-string equality matched this before the lexicon existed, and no lexicon may take it away");
    }

    // ---- the user-visible bug ----------------------------------------------------------------------

    // THE BUG THIS MILESTONE EXISTS TO FIX, named as such.
    //
    // A candidate who listed "React.js" was told to ADD "React" — at Critical priority, with an exact
    // Impact beside it, in the section they were reading for advice. Authoritative-looking and wrong in
    // the direction that costs them: they either add a duplicate entry or conclude the product cannot
    // read their CV.
    //
    // Both halves are asserted in one test on purpose. The "before" is what makes the "after" mean
    // something — without it, an implementation that emitted no skill advice at all would pass.
    [Fact]
    public void Advice_ForASkillTheCandidateAlreadyListedUnderAnAlias_IsNoLongerEmitted()
    {
        var resume = ResumeWithSkill("React.js");
        var jobPosting = PostingRequiring("React");

        var before = new ScoringEngine(FakeSkillLexicon.Empty).Score(resume, jobPosting, ReferenceDate);
        var missing = before.Recommendations.Should()
            .ContainSingle(r => r.Kind == RecommendationKind.MissingMustHaveSkill).Subject;
        missing.Message.Should().Contain("'React'");
        missing.Priority.Should().Be(RecommendationPriority.Critical,
            "an unmet must-have is Critical whatever its impact — which is what made the wrong advice look authoritative");

        var after = new ScoringEngine(Lexicon).Score(resume, jobPosting, ReferenceDate);
        after.Recommendations.Should().NotContain(r => r.Kind == RecommendationKind.MissingMustHaveSkill,
            "the candidate already listed this skill, under another spelling");
    }

    // The advice and the score are computed from the same lexicon instance, so they cannot disagree about
    // what the candidate has. RecommendationBuilder re-evaluates ScoringRules to derive its Impact, which
    // is exactly where a second lexicon would reintroduce the bug one layer up.
    [Fact]
    public void Advice_AndTheScore_AgreeAboutWhichRequirementsAreMet()
    {
        var resume = ResumeWithSkill("React.js");
        var jobPosting = PostingRequiring("React");
        jobPosting.AddRequirement(JobRequirement.Create(Technology.Create("Fortran"), RequirementPriority.MustHave));

        var result = new ScoringEngine(Lexicon).Score(resume, jobPosting, ReferenceDate);

        result.Breakdown.SkillsScore.Should().Be(0.5, "one of the two must-haves is met");
        result.Recommendations.Where(r => r.Kind == RecommendationKind.MissingMustHaveSkill)
            .Select(r => r.Message).Should().ContainSingle().Which.Should().Contain("'Fortran'");
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static double SkillsScoreFor(Resume resume) => SkillsScoreFor(resume, Lexicon);

    private static double SkillsScoreFor(Resume resume, ISkillLexicon lexicon, string required = "React") =>
        new ScoringEngine(lexicon).Score(resume, PostingRequiring(required), ReferenceDate).Breakdown.SkillsScore;

    private static JobPosting PostingRequiring(string skill)
    {
        var jobPosting = JobPosting.Create(AccountId.New(), "Frontend Developer", OrganizationName.Create("Acme"));
        jobPosting.AddRequirement(JobRequirement.Create(Technology.Create(skill), RequirementPriority.MustHave));
        return jobPosting;
    }

    private static Resume EmptyResume()
    {
        var contact = new ContactInformation(PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
        return Resume.Create(AccountId.New(), contact);
    }

    private static Resume ResumeWithSkill(string name)
    {
        var resume = EmptyResume();
        resume.AddSkill(Skill.Create(Technology.Create(name)));
        return resume;
    }

    private static Resume ResumeWithSkillKeyword(string keyword)
    {
        var resume = EmptyResume();
        resume.AddSkill(Skill.Create(Technology.Create(Unrelated)) with { Keywords = [keyword] });
        return resume;
    }

    private static Resume ResumeWithProjectTechnology(string technology)
    {
        var resume = EmptyResume();
        resume.AddProject(new Project("A project", DateRange.Create(ReferenceDate.AddYears(-1)))
        {
            Technologies = [Technology.Create(technology)],
        });
        return resume;
    }
}

using BuildCv.Application.Common.Services;
using BuildCv.Application.Scoring;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Scoring;

// What the engine publishes about WHICH entry answered which requirement.
//
// The whole reason this exists is that a client cannot work it out: IsSatisfiedBy canonicalizes through
// SkillLexicon.txt, an embedded resource served by no endpoint, so a client comparing strings would
// contradict the score beside it exactly when the lexicon did its job. These tests are therefore mostly
// about the cases where the published answer DIFFERS from naive string equality -- an identical-spelling
// match would pass whether attribution consulted the lexicon or not.
public class RequirementAttributionTests
{
    private static readonly DateOnly ReferenceDate = new(2025, 1, 1);

    private static readonly ISkillLexicon Lexicon = FakeSkillLexicon.With(
        ("react.js", "React"),
        ("reactjs", "React"));

    private const string Unrelated = "Zzz Unrelated Placeholder";

    // The heart of it: the candidate wrote "React.js", the posting asked for "React", and the response
    // says so IN THE CANDIDATE'S WORDING. Returning the requirement's spelling back would be a match the
    // candidate cannot find anywhere in their own CV.
    [Fact]
    public void Attribute_ASkillNameThatIsAnAliasOfTheRequirement_ReportsTheCandidatesOwnWording()
    {
        var attribution = Attribute(ResumeWithSkill("React.js"), JobPostingRequiring("React"));

        attribution.Should().ContainSingle();
        attribution[0].Skill.Should().Be("React", "the requirement is reported as the posting stated it");
        attribution[0].Satisfied.Should().BeTrue();
        attribution[0].MatchedBy.Should().ContainSingle();
        attribution[0].MatchedBy[0].Source.Should().Be(RequirementMatchSource.SkillName);
        attribution[0].MatchedBy[0].MatchedText.Should().Be(
            "React.js", "the candidate has to be able to find this string in their own CV");
    }

    // One entry per requirement, satisfied or not. This is what replaces reading absence out of the
    // recommendation text: advice is capped at ten, so a requirement going unmentioned never meant it
    // matched.
    [Fact]
    public void Attribute_ARequirementNothingAnswers_IsReportedAsUnsatisfiedRatherThanOmitted()
    {
        var attribution = Attribute(ResumeWithSkill(Unrelated), JobPostingRequiring("React"));

        attribution.Should().ContainSingle("an unanswered requirement is still a requirement");
        attribution[0].Satisfied.Should().BeFalse();
        attribution[0].MatchedBy.Should().BeEmpty();
    }

    // One test per comparison site, for the same reason SkillLexiconMatchingTests has one: the three sites
    // are separate loops, and reporting two of them while forgetting the third is a one-line mistake that
    // a single general test would survive. Each resume here can only match at its own site.
    [Fact]
    public void Attribute_AKeywordBesideAnUnrelatedSkill_IsReportedAsAKeyword()
    {
        var attribution = Attribute(ResumeWithSkillKeyword("reactjs"), JobPostingRequiring("React"));

        attribution[0].Satisfied.Should().BeTrue();
        attribution[0].MatchedBy.Should().ContainSingle();
        attribution[0].MatchedBy[0].Source.Should().Be(RequirementMatchSource.SkillKeyword);
        attribution[0].MatchedBy[0].MatchedText.Should().Be("reactjs");
    }

    [Fact]
    public void Attribute_ATechnologyOnAProject_IsReportedAsAProjectTechnology()
    {
        var attribution = Attribute(ResumeWithProjectTechnology("React.js"), JobPostingRequiring("React"));

        attribution[0].Satisfied.Should().BeTrue();
        attribution[0].MatchedBy.Should().ContainSingle();
        attribution[0].MatchedBy[0].Source.Should().Be(RequirementMatchSource.ProjectTechnology);
        attribution[0].MatchedBy[0].MatchedText.Should().Be("React.js");
    }

    // Every place that answered, not the first one found. A client showing the candidate why a requirement
    // counted should be able to show all of it.
    [Fact]
    public void Attribute_ARequirementAnsweredInTwoPlaces_ReportsBoth()
    {
        var resume = ResumeWithSkill("React.js");
        resume.AddProject(new Project("A project", DateRange.Create(ReferenceDate.AddYears(-1)))
        {
            Technologies = [Technology.Create("reactjs")],
        });

        var attribution = Attribute(resume, JobPostingRequiring("React"));

        attribution[0].MatchedBy.Should().HaveCount(2);
        attribution[0].MatchedBy.Select(evidence => evidence.Source).Should().BeEquivalentTo(
            [RequirementMatchSource.SkillName, RequirementMatchSource.ProjectTechnology]);
    }

    // THE PROPERTY THAT MAKES ATTRIBUTION TRUSTWORTHY: it is not a second opinion. Satisfied comes from the
    // same comparer that scored, so the two cannot disagree -- which is the failure the frontend's
    // string-matching workaround has today and the reason this shipped at all.
    [Theory]
    [InlineData("React.js", true)]
    [InlineData("reactjs", true)]
    [InlineData("React", true)]
    [InlineData("Vue", false)]
    public void Attribute_AgreesWithTheScoreAboutWhetherARequirementWasMet(string skillName, bool expected)
    {
        var resume = ResumeWithSkill(skillName);
        var jobPosting = JobPostingRequiring("React");

        var engine = new ScoringEngine(Lexicon);
        var attribution = engine.Attribute(resume, jobPosting);
        // Compared against the score the CLIENT is shown, not against the internal rule -- the point is
        // that the two halves of one response agree, and a comparison against ScoringRules would only
        // prove the rule agrees with itself.
        var skillsScore = engine.Score(resume, jobPosting, ReferenceDate).Breakdown.SkillsScore;

        attribution[0].Satisfied.Should().Be(expected);
        (skillsScore > 0).Should().Be(
            expected, "attribution and the score are the same comparison, not two");
    }

    // Attribution reads and reports; it must not participate in the number. Asserted by scoring the same
    // pair twice with attribution requested in between -- a rule that mutated shared state or consumed an
    // enumerable would show up here.
    [Fact]
    public void Attribute_DoesNotChangeTheScoreItDescribes()
    {
        var engine = new ScoringEngine(Lexicon);
        var resume = ResumeWithSkill("React.js");
        var jobPosting = JobPostingRequiring("React");

        var before = engine.Score(resume, jobPosting, ReferenceDate).Breakdown.WeightedTotal;
        engine.Attribute(resume, jobPosting);
        var after = engine.Score(resume, jobPosting, ReferenceDate).Breakdown.WeightedTotal;

        after.Should().Be(before);
    }

    // A posting that asks for nothing gets an empty list, which is a different fact from the API's null --
    // "the posting required nothing" rather than "this response does not carry attribution".
    [Fact]
    public void Attribute_APostingWithNoRequirements_IsEmptyRatherThanNull()
    {
        var jobPosting = JobPosting.Create(AccountId.New(), "Frontend Developer", OrganizationName.Create("Acme"));

        Attribute(ResumeWithSkill("React"), jobPosting).Should().BeEmpty();
    }

    private static IReadOnlyList<RequirementAttribution> Attribute(Resume resume, JobPosting jobPosting) =>
        new ScoringEngine(Lexicon).Attribute(resume, jobPosting);

    private static JobPosting JobPostingRequiring(string skill)
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

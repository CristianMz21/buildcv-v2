using BuildCv.Application.Readability;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Readability;

// What each section MEASURES, both directions. What acting on the advice is WORTH is
// ActingOnAReadabilityRecommendationTests; what is emitted is ReadabilityRecommendationBuilderTests.
public class ReadabilityEngineTests
{
    private static readonly DateOnly ReferenceDate = ReadabilityTestResumes.ReferenceDate;

    private readonly ReadabilityEngine _engine = new();

    private ReadabilityBreakdown BreakdownOf(Resume resume) => _engine.Evaluate(resume, ReferenceDate).Breakdown;

    // THE MILESTONE'S REASON FOR EXISTING, stated as a signature rather than a scenario: Evaluate takes a
    // resume and a date. There is no JobPosting parameter to pass, so a readability run cannot depend on
    // a posting existing — which is what makes this the half of the product a candidate can use the
    // moment they upload a CV. The system-wide version of the claim is
    // ReadabilityEndpointTests.Readability_WithNoJobPostingInTheSystem_StillAnswers.
    [Fact]
    public void Evaluate_takes_no_job_posting_and_answers_from_the_resume_alone()
    {
        var result = _engine.Evaluate(ReadabilityTestResumes.FullyPopulated(), ReferenceDate);

        result.Should().NotBeNull();
        result.Breakdown.Should().NotBeNull();
    }

    [Fact]
    public void Evaluate_rejects_a_null_resume()
    {
        var act = () => _engine.Evaluate(null!, ReferenceDate);

        act.Should().Throw<ArgumentNullException>();
    }

    // A COMPLETE CV SCORES 1.0. Only reachable because AtsParseability is renormalized out: left
    // weighted against a hard zero it would cap this at 0.90.
    [Fact]
    public void A_fully_populated_resume_scores_one()
    {
        var result = _engine.Evaluate(ReadabilityTestResumes.FullyPopulated(), ReferenceDate);

        result.Breakdown.CompletenessScore.Should().Be(1.0);
        result.Breakdown.ContactScore.Should().Be(1.0);
        result.Breakdown.AchievementsScore.Should().Be(1.0);
        result.Breakdown.ChronologyScore.Should().Be(1.0);
        result.WeightedTotal.Should().BeApproximately(1.0, 1e-12);
    }

    // AND A COMPLETE CV NEEDS NO ADVICE. Without this, "scores 1.0" would be compatible with an engine
    // that still handed the candidate a list of things to fix.
    [Fact]
    public void A_fully_populated_resume_produces_no_advice()
    {
        _engine.Evaluate(ReadabilityTestResumes.FullyPopulated(), ReferenceDate)
            .Recommendations.Should().BeEmpty();
    }

    // THE DEGENERATE CASE. Every section divides by something that is zero here — three expected
    // sections, three contact channels, no highlights, no experience entries — so this is the test that
    // says none of those divisions happens.
    [Fact]
    public void An_empty_resume_scores_zero_without_throwing_or_dividing_by_zero()
    {
        var result = _engine.Evaluate(ReadabilityTestResumes.Empty(), ReferenceDate);

        result.Breakdown.CompletenessScore.Should().Be(0.0);
        result.Breakdown.ContactScore.Should().Be(0.0);
        result.Breakdown.AchievementsScore.Should().Be(0.0);
        result.Breakdown.ChronologyScore.Should().Be(0.0);
        result.WeightedTotal.Should().Be(0.0);
        double.IsFinite(result.WeightedTotal).Should().BeTrue("a NaN total would poison the band too");
    }

    // AND IT STILL PRODUCES ADVICE. A score of zero with an empty advice list is the one outcome that
    // would make this feature useless to the candidate who needs it most.
    [Fact]
    public void An_empty_resume_still_produces_advice()
    {
        _engine.Evaluate(ReadabilityTestResumes.Empty(), ReferenceDate)
            .Recommendations.Should().NotBeEmpty();
    }

    // THE RENORMALIZATION, at the layer that decides it. ReadabilityWeightsSnapshotTests proves the
    // arithmetic over every subset; this proves the ENGINE asks for the subset without AtsParseability.
    [Fact]
    public void Ats_parseability_is_renormalized_out_and_the_remaining_weights_still_sum_to_one()
    {
        var weights = BreakdownOf(ReadabilityTestResumes.FullyPopulated()).Weights;

        weights.AtsParseability.Should().Be(0.0,
            "no resume carries import signals yet, so the section could not be measured");
        (weights.Completeness + weights.Contact + weights.Achievements + weights.Chronology)
            .Should().BeApproximately(1.0, 1e-12,
                "the four that remain carry the whole score, so the ceiling stays 1.00");
    }

    // The wire's side of the same fact: a section that could not be measured shows a weight of 0 beside
    // a score that means nothing, and there is deliberately no second flag saying so.
    [Fact]
    public void The_ats_parseability_section_reports_a_zero_score_beside_its_zero_weight()
    {
        var sections = BreakdownOf(ReadabilityTestResumes.FullyPopulated()).Sections;

        var ats = sections.Should()
            .ContainSingle(section => section.Section == ReadabilitySectionType.AtsParseability).Subject;
        ats.Weight.Should().Be(0.0);
        ats.Score.Should().Be(0.0);
    }

    // Completeness: education, skills, summary.
    [Theory]
    [InlineData(false, false, false, 0.0)]
    [InlineData(true, false, false, 1.0 / 3.0)]
    [InlineData(true, true, false, 2.0 / 3.0)]
    [InlineData(true, true, true, 1.0)]
    public void Completeness_counts_the_three_sections_an_ats_expects_beside_the_work_history(
        bool education, bool skills, bool summary, double expected)
    {
        var resume = ReadabilityTestResumes.Empty();
        if (education)
        {
            resume.AddEducation(new Education(
                OrganizationName.Create("UBA"), "Ingeniería", null,
                DateRange.Create(new DateOnly(2013, 3, 1), new DateOnly(2018, 12, 1)), null));
        }

        if (skills)
            resume.AddSkill(Skill.Create(Technology.Create("C#")));
        if (summary)
            resume.UpdateContactInformation(resume.ContactInformation with { Summary = "Backend engineer." });

        BreakdownOf(resume).CompletenessScore.Should().BeApproximately(expected, 1e-12);
    }

    // Name and email are NOT counted, in either section, because ContactInformation requires both: a
    // term that is always 1 measures nothing while quietly making every other term worth less. The
    // emptiest possible resume scoring 0.0 above is what executes that claim.
    [Theory]
    [InlineData(false, false, false, 0.0)]
    [InlineData(true, false, false, 1.0 / 3.0)]
    [InlineData(true, true, false, 2.0 / 3.0)]
    [InlineData(true, true, true, 1.0)]
    public void Contact_counts_the_three_optional_ways_to_reach_a_candidate(
        bool phone, bool location, bool onlinePresence, double expected)
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.UpdateContactInformation(resume.ContactInformation with
        {
            PhoneNumber = phone ? PhoneNumber.Create("+541155501234") : null,
            Location = location ? "Buenos Aires" : null,
            Website = onlinePresence ? Url.Create("https://janedoe.dev") : null,
        });

        BreakdownOf(resume).ContactScore.Should().BeApproximately(expected, 1e-12);
    }

    // A profile counts as the same channel a website does. Counting them separately would tell a
    // candidate who linked their GitHub that they are still one third short.
    [Fact]
    public void Contact_treats_a_profile_as_the_same_channel_a_website_is()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.UpdateContactInformation(resume.ContactInformation with
        {
            Profiles = [new Profile("GitHub", "janedoe", Url.Create("https://github.com/janedoe"))],
        });

        BreakdownOf(resume).ContactScore.Should().BeApproximately(1.0 / 3.0, 1e-12);
    }

    // Achievements: half for numbers, half for action verbs, over the same denominator.
    [Theory]
    [InlineData(new[] { "Reduced latency by 40%" }, 1.0)]
    [InlineData(new[] { "Reduced latency" }, 0.5)]
    [InlineData(new[] { "Responsible for 3 services" }, 0.5)]
    [InlineData(new[] { "Responsible for services" }, 0.0)]
    [InlineData(new[] { "Reduced latency by 40%", "Responsible for services" }, 0.5)]
    public void Achievements_scores_quantification_and_action_verbs_over_the_highlights(
        string[] highlights, double expected)
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(new DateOnly(2022, 1, 1), new DateOnly(2024, 1, 1)))
        {
            Highlights = highlights,
        });

        BreakdownOf(resume).AchievementsScore.Should().BeApproximately(expected, 1e-12);
    }

    // The product's users write their CVs in Spanish. An English-only verb list would tell every one of
    // them their resume states no achievements — authoritative, and wrong in the direction that costs
    // the candidate.
    [Theory]
    [InlineData("Lideré un equipo de 6 personas")]
    [InlineData("lidere un equipo de 6 personas")]
    [InlineData("• Migré 12 servicios a .NET 8")]
    public void Achievements_recognises_a_spanish_action_verb_whatever_its_accents_and_bullet(string highlight)
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(new DateOnly(2022, 1, 1), new DateOnly(2024, 1, 1)))
        {
            Highlights = [highlight],
        });

        BreakdownOf(resume).AchievementsScore.Should().Be(1.0);
    }

    // A leading digit is NOT skipped on the way to the first word: "3 servers were migrated" does not
    // begin with a verb, and pretending it does would pay a candidate for the wrong edit.
    [Fact]
    public void Achievements_does_not_read_past_a_leading_number_to_find_a_verb()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(new DateOnly(2022, 1, 1), new DateOnly(2024, 1, 1)))
        {
            Highlights = ["3 servers migrated to Kubernetes"],
        });

        BreakdownOf(resume).AchievementsScore.Should().Be(0.5, "it states a number but does not lead with a verb");
    }

    [Fact]
    public void Chronology_gives_a_single_role_its_ceiling_because_one_entry_has_no_break()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(Role("Acme", "Backend Developer", 2022, 2024));

        BreakdownOf(resume).ChronologyScore.Should().Be(1.0);
    }

    [Fact]
    public void Chronology_counts_an_entry_that_opens_after_a_long_break_as_broken()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(Role("Acme", "Backend Developer", 2018, 2019));
        resume.AddExperience(Role("Globex", "Senior Backend Developer", 2021, 2023));

        BreakdownOf(resume).ChronologyScore.Should().BeApproximately(0.5, 1e-12,
            "the first entry always counts and the second opens two years later");
    }

    // Six months is the threshold, and both sides of it are exercised so a test that moved it would
    // notice. 183 days after 2019-01-01 is 2019-07-03.
    [Theory]
    [InlineData(183, 1.0)]
    [InlineData(184, 0.5)]
    public void Chronology_tolerates_a_break_of_up_to_six_months(int gapDays, double expected)
    {
        var resume = ReadabilityTestResumes.Empty();
        var firstEnd = new DateOnly(2019, 1, 1);
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(new DateOnly(2018, 1, 1), firstEnd)));
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Globex"), "Senior Backend Developer",
            DateRange.Create(firstEnd.AddDays(gapDays), firstEnd.AddDays(gapDays + 365))));

        BreakdownOf(resume).ChronologyScore.Should().BeApproximately(expected, 1e-12);
    }

    // OVERLAPS ARE NEVER BREAKS, and the walk carries the furthest end reached rather than the previous
    // entry's. Without that, a long role held alongside a short one would manufacture a gap out of the
    // short one's end date — a resume punished for saying MORE about itself.
    [Fact]
    public void Chronology_measures_the_break_against_everything_before_it_not_the_previous_entry()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(Role("Acme", "Backend Developer", 2018, 2024));
        resume.AddExperience(Role("Side Project SRL", "Consultant", 2019, 2020));
        resume.AddExperience(Role("Globex", "Senior Backend Developer", 2024, 2025));

        BreakdownOf(resume).ChronologyScore.Should().Be(1.0,
            "the six-year role covers the years after the consultancy ended");
    }

    // An open-ended entry runs to the reference date, the same reading ScoringRules gives a period with
    // no end. Without it, a candidate's CURRENT role would look like a role that ended on its start date
    // and every later entry would read as a break.
    [Fact]
    public void Chronology_reads_an_open_ended_role_as_running_to_the_reference_date()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Acme"), "Backend Developer",
            DateRange.Create(new DateOnly(2018, 1, 1))));
        resume.AddExperience(Role("Globex", "Consultant", 2024, 2024));

        BreakdownOf(resume).ChronologyScore.Should().Be(1.0);
    }

    // VOLUNTEER TIME COUNTS HERE, unlike in the scoring engine where only Professional days do. The
    // question this section asks is "can a recruiter read your timeline", and an unpaid year is still an
    // explained year.
    [Fact]
    public void Chronology_counts_a_volunteer_entry_as_part_of_the_timeline()
    {
        var resume = ReadabilityTestResumes.Empty();
        resume.AddExperience(Role("Acme", "Backend Developer", 2018, 2019));
        resume.AddExperience(new Experience(
            ExperienceType.Volunteer, OrganizationName.Create("Cruz Roja"), "Volunteer",
            DateRange.Create(new DateOnly(2019, 1, 1), new DateOnly(2021, 1, 1))));
        resume.AddExperience(Role("Globex", "Senior Backend Developer", 2021, 2023));

        BreakdownOf(resume).ChronologyScore.Should().Be(1.0);
    }

    private static Experience Role(string organization, string position, int startYear, int endYear) =>
        new(ExperienceType.Professional,
            OrganizationName.Create(organization),
            position,
            DateRange.Create(new DateOnly(startYear, 1, 1), new DateOnly(endYear, 1, 1)));
}

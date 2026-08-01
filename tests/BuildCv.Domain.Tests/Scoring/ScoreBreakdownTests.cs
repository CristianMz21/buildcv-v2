using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

public class ScoreBreakdownTests
{
    private static readonly ScoringWeightsSnapshot DefaultWeights = ScoringWeightsSnapshot.Default();

    [Fact]
    public void WeightedTotal_calculates_correctly()
    {
        var breakdown = ScoreBreakdown.Create(
            skillsScore: 0.9,
            experienceScore: 0.8,
            educationScore: 0.7,
            certificationsScore: 0.6,
            projectsScore: 1.0,
            languagesScore: 0.3,
            weights: DefaultWeights);

        var total = breakdown.WeightedTotal;

        var expected = 0.45 * 0.9 + 0.20 * 0.8 + 0.10 * 0.7 + 0.10 * 0.6 + 0.05 * 1.0 + 0.10 * 0.3;
        total.Should().BeApproximately(expected, 0.001);
    }

    // The sixth term, on its own. Every other total test moves five scores and Languages together, so
    // a WeightedTotal that simply forgot the Languages term would still satisfy them whenever the
    // other five happened to account for the difference. Here nothing else is non-zero: the answer is
    // the Languages weight or it is zero.
    [Fact]
    public void WeightedTotal_countsLanguages_evenWhenEveryOtherSectionScoresZero()
    {
        var breakdown = ScoreBreakdown.Create(0.0, 0.0, 0.0, 0.0, 0.0, 1.0, DefaultWeights);

        breakdown.WeightedTotal.Should().BeApproximately(DefaultWeights.Languages, 0.0001);
        breakdown.WeightedTotal.Should().BeGreaterThan(0.0, "a dropped Languages term reads as a flat zero");
    }

    [Fact]
    public void WeightedTotal_with_zero_scores()
    {
        var breakdown = ScoreBreakdown.Create(
            skillsScore: 0.0,
            experienceScore: 0.0,
            educationScore: 0.0,
            certificationsScore: 0.0,
            projectsScore: 0.0,
            languagesScore: 0.0,
            weights: DefaultWeights);

        breakdown.WeightedTotal.Should().Be(0.0);
    }

    [Fact]
    public void WeightedTotal_with_perfect_scores()
    {
        var breakdown = ScoreBreakdown.Create(
            skillsScore: 1.0,
            experienceScore: 1.0,
            educationScore: 1.0,
            certificationsScore: 1.0,
            projectsScore: 1.0,
            languagesScore: 1.0,
            weights: DefaultWeights);

        breakdown.WeightedTotal.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ScoreBreakdown_rejects_score_below_zero()
    {
        var act = () => ScoreBreakdown.Create(
            skillsScore: -0.1,
            experienceScore: 0.0,
            educationScore: 0.0,
            certificationsScore: 0.0,
            projectsScore: 0.0,
            languagesScore: 0.0,
            weights: DefaultWeights);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ScoreBreakdown_rejects_score_above_one()
    {
        var act = () => ScoreBreakdown.Create(
            skillsScore: 1.5,
            experienceScore: 0.0,
            educationScore: 0.0,
            certificationsScore: 0.0,
            projectsScore: 0.0,
            languagesScore: 0.0,
            weights: DefaultWeights);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ScoreBreakdown_rejects_a_languages_score_outside_the_unit_interval()
    {
        var act = () => ScoreBreakdown.Create(0.0, 0.0, 0.0, 0.0, 0.0, 1.5, DefaultWeights);

        act.Should().Throw<ArgumentException>().WithParameterName("languagesScore");
    }

    [Fact]
    public void ScoreBreakdown_default_snapshot_has_expected_weights()
    {
        DefaultWeights.Skills.Should().Be(0.45);
        DefaultWeights.Experience.Should().Be(0.20);
        DefaultWeights.Education.Should().Be(0.10);
        DefaultWeights.Certifications.Should().Be(0.10);
        DefaultWeights.Projects.Should().Be(0.05);
        DefaultWeights.Languages.Should().Be(0.10);
        DefaultWeights.SchemaVersion.Should().Be(2);
    }

    // The invariant everything downstream leans on. WeightedTotal is only a 0..1 number because the
    // six weights sum to one, and only then is Analysis.OverallScore a percentage and ScoreBand's
    // thresholds meaningful.
    [Fact]
    public void ScoringWeightsSnapshot_default_weights_still_sum_to_one_across_six_sections()
    {
        var sections = Enum.GetValues<SectionType>();

        sections.Sum(DefaultWeights.WeightFor).Should().BeApproximately(1.0, 0.0001);
        sections.Should().OnlyContain(section => DefaultWeights.WeightFor(section) >= 0.0);
    }

    [Fact]
    public void ScoringWeightsSnapshot_rejects_weights_that_do_not_sum_to_one()
    {
        var act = () => ScoringWeightsSnapshot.Create(0.5, 0.2, 0.2, 0.1, 0.05, 0.10);

        act.Should().Throw<ArgumentException>();
    }

    // A five-section snapshot is exactly what a v1 payload deserializes into, so the factory has to
    // accept it rather than reject the history.
    [Fact]
    public void ScoringWeightsSnapshot_accepts_a_zero_languages_weight_when_the_other_five_sum_to_one()
    {
        var weights = ScoringWeightsSnapshot.Create(0.45, 0.20, 0.20, 0.10, 0.05, 0.0, schemaVersion: 1);

        weights.Languages.Should().Be(0.0);
        weights.SchemaVersion.Should().Be(1);
    }

    [Theory]
    [InlineData(SectionType.Skills, 0.1)]
    [InlineData(SectionType.Experience, 0.2)]
    [InlineData(SectionType.Education, 0.3)]
    [InlineData(SectionType.Certifications, 0.4)]
    [InlineData(SectionType.Projects, 0.5)]
    [InlineData(SectionType.Languages, 0.6)]
    public void ScoreFor_returns_the_column_that_section_names(SectionType section, double expected)
    {
        var breakdown = ScoreBreakdown.Create(0.1, 0.2, 0.3, 0.4, 0.5, 0.6, DefaultWeights);

        breakdown.ScoreFor(section).Should().Be(expected);
    }

    // Guards the theory above: it enumerates six members by hand, so a seventh SectionType has to
    // fail here rather than be silently unexercised.
    [Fact]
    public void ScoreFor_answers_for_every_declared_section()
    {
        var breakdown = ScoreBreakdown.Create(0.1, 0.2, 0.3, 0.4, 0.5, 0.6, DefaultWeights);

        var sections = Enum.GetValues<SectionType>();

        sections.Should().HaveCount(6, "the ScoreFor theory names each section explicitly");
        foreach (var section in sections)
            breakdown.Invoking(entry => entry.ScoreFor(section)).Should().NotThrow();
    }

    [Fact]
    public void Sections_pairs_each_score_with_the_weight_it_was_counted_under()
    {
        var breakdown = ScoreBreakdown.Create(0.1, 0.2, 0.3, 0.4, 0.5, 0.6, DefaultWeights);

        breakdown.Sections.Should().Equal(
            SectionScore.Create(SectionType.Skills, 0.1, 0.45),
            SectionScore.Create(SectionType.Experience, 0.2, 0.20),
            SectionScore.Create(SectionType.Education, 0.3, 0.10),
            SectionScore.Create(SectionType.Certifications, 0.4, 0.10),
            SectionScore.Create(SectionType.Projects, 0.5, 0.05),
            SectionScore.Create(SectionType.Languages, 0.6, 0.10));
    }

    // The projection is only worth anything if it agrees with the number the candidate is shown.
    [Fact]
    public void Sections_reproduce_the_weighted_total_when_summed()
    {
        var breakdown = ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 1.0, 0.3, DefaultWeights);

        breakdown.Sections.Sum(section => section.Score * section.Weight)
            .Should().BeApproximately(breakdown.WeightedTotal, 0.0001);
    }

    [Fact]
    public void ScoreBreakdown_is_immutable()
    {
        var b1 = ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights);

        b1.SkillsScore.Should().Be(0.5);
    }
}

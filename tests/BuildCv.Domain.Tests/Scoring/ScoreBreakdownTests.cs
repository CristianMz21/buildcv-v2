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

    // The sixth term, on its own, under weights that are deliberately NOT the shipped ones.
    //
    // Default() now weights Languages at 0.10, so this could read the defaults and still discriminate.
    // It states its own snapshot anyway: an explicit set cannot stop discriminating because someone
    // redistributed Default(), which is exactly how this test lost its bite the last time.
    [Fact]
    public void WeightedTotal_countsLanguages_whenTheWeightsGiveItAny()
    {
        var weighted = ScoringWeightsSnapshot.Create(0.40, 0.20, 0.20, 0.10, 0.05, 0.05);
        var breakdown = ScoreBreakdown.Create(0.0, 0.0, 0.0, 0.0, 0.0, 1.0, weighted);

        breakdown.WeightedTotal.Should().BeApproximately(0.05, 0.0001);
        breakdown.WeightedTotal.Should().BeGreaterThan(0.0, "a dropped Languages term reads as a flat zero");
    }

    // THE INVERSE of the assertion that used to stand here.
    //
    // Until Languages carried weight, this file pinned the opposite: that no Languages score could
    // move the total, because that was what made shaping the section behaviour-neutral. Weight and
    // score arrived together in this release, so the property is now the other one — a Languages score
    // moves the total by exactly its weight, and a candidate who speaks the language a posting asks
    // for is measurably better off for it.
    //
    // Kept as the same theory over the same three scores so the flip is visible in the diff rather
    // than looking like a deleted test.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void WeightedTotal_underTheShippedWeights_movesWithTheLanguagesScore(double languagesScore)
    {
        var breakdown = ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 1.0, languagesScore, DefaultWeights);

        var withoutLanguages = 0.45 * 0.9 + 0.20 * 0.8 + 0.10 * 0.7 + 0.10 * 0.6 + 0.05 * 1.0;

        breakdown.WeightedTotal.Should().BeApproximately(withoutLanguages + (0.10 * languagesScore), 0.0001);
    }

    // The guard on the theory above: at languagesScore 0.0 it asserts "unchanged", which is also what a
    // dropped Languages term produces, so THAT ROW stays green against the very regression the theory
    // exists to catch. Measured, not assumed — deleting the Languages term from WeightedTotal fails the
    // 0.5 and 1.0 rows and leaves the 0.0 row passing. One green row out of three is not a hole while
    // the other two bite; this test exists so the discrimination is stated once, on its own, where
    // deleting it is a visible act rather than a theory row quietly going away.
    [Fact]
    public void WeightedTotal_underTheShippedWeights_differsAcrossLanguagesScores()
    {
        double TotalFor(double languagesScore) =>
            ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 1.0, languagesScore, DefaultWeights).WeightedTotal;

        TotalFor(1.0).Should().BeApproximately(TotalFor(0.0) + 0.10, 0.0001,
            "a dropped Languages term reads as a flat zero for every score");
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
        DefaultWeights.Education.Should().Be(0.10, "v2 halved this to fund the Languages section");
        DefaultWeights.Certifications.Should().Be(0.10);
        DefaultWeights.Projects.Should().Be(0.05);
        DefaultWeights.Languages.Should().Be(0.10, "the section is now weighted AND computed");
        DefaultWeights.SchemaVersion.Should().Be(2, "the weighting moved, so the version has to say so");
    }

    // The SHAPE of the v1 → v2 redistribution, which is stronger than the per-weight assertion above.
    //
    // Exactly one tenth moved, and it moved between exactly two sections. Four of the five weights
    // that were already explaining live scores are untouched, and the fifth plus the new one still
    // add up to what the fifth was worth on its own. A redistribution that also shaved Skills would
    // satisfy the sum-to-one invariant and every band test, and would only be visible here.
    //
    // SCOPED PRECISELY, because it looks stronger than it is: this test CANNOT tell v1 from v2. Under
    // v1 the same four weights hold and Education + Languages is 0.20 + 0.00, which is the same 0.20 —
    // a negative control that reverted the redistribution left this green. Naming WHICH of the two
    // shipped is the job of ScoreBreakdown_default_snapshot_has_expected_weights, directly above.
    [Fact]
    public void ScoringWeightsSnapshot_v2_moved_weight_from_education_to_languages_and_nowhere_else()
    {
        DefaultWeights.Skills.Should().Be(0.45, "v1 weighted Skills at 0.45 and nothing here changes that");
        DefaultWeights.Experience.Should().Be(0.20, "v1 weighted Experience at 0.20");
        DefaultWeights.Certifications.Should().Be(0.10, "v1 weighted Certifications at 0.10");
        DefaultWeights.Projects.Should().Be(0.05, "v1 weighted Projects at 0.05");

        (DefaultWeights.Education + DefaultWeights.Languages).Should().BeApproximately(0.20, 0.0001,
            "the tenth Languages now carries came out of Education and out of nothing else");
    }

    // The invariant everything downstream leans on. WeightedTotal is only a 0..1 number because the
    // six weights sum to one, and only then is Analysis.OverallScore a percentage and ScoreBand's
    // thresholds meaningful.
    //
    // NOT SUFFICIENT ON ITS OWN, and this is the test that proved it: the weights that once quietly
    // cost every educated candidate ten points summed to 1.0 too, so this stayed green throughout. Any
    // redistribution satisfies it. The test above is the one that pins WHICH redistribution shipped,
    // which is why the two live next to each other.
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
    //
    // Scoped precisely: both sides read the SAME weights, so this is self-consistent by construction
    // and can never catch a weight regression — it catches Sections drifting from WeightedTotal, and
    // nothing else. The weights themselves are pinned by
    // ScoringWeightsSnapshot_default_leaves_the_five_scored_sections_exactly_as_they_were.
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

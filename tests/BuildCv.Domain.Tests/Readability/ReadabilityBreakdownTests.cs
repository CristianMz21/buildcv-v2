using BuildCv.Domain.Readability;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Readability;

public class ReadabilityBreakdownTests
{
    private static readonly ReadabilityWeightsSnapshot Default = ReadabilityWeightsSnapshot.Default();

    private static ReadabilityBreakdown Breakdown(
        double completeness = 0.0,
        double contact = 0.0,
        double achievements = 0.0,
        double chronology = 0.0,
        double atsParseability = 0.0,
        ReadabilityWeightsSnapshot? weights = null) =>
        ReadabilityBreakdown.Create(
            completeness, contact, achievements, chronology, atsParseability, weights ?? Default);

    [Fact]
    public void WeightedTotal_is_the_sum_of_each_score_times_its_own_weight()
    {
        var breakdown = Breakdown(1.0, 0.5, 0.25, 0.0, 1.0);

        // 0.30 * 1.0 + 0.20 * 0.5 + 0.25 * 0.25 + 0.15 * 0.0 + 0.10 * 1.0
        breakdown.WeightedTotal.Should().BeApproximately(0.30 + 0.10 + 0.0625 + 0.0 + 0.10, 1e-12);
    }

    // The projection every consumer reads through. It has to pair each score with the weight from THIS
    // snapshot, in enum order, so nobody has to do that pairing by hand.
    [Fact]
    public void Sections_pairs_every_member_with_its_own_score_and_weight()
    {
        var breakdown = Breakdown(0.1, 0.2, 0.3, 0.4, 0.5);

        var sections = breakdown.Sections;

        sections.Should().HaveCount(Enum.GetValues<ReadabilitySectionType>().Length);
        sections.Select(section => section.Section).Should().Equal(Enum.GetValues<ReadabilitySectionType>());

        foreach (var section in sections)
        {
            section.Score.Should().Be(breakdown.ScoreFor(section.Section));
            section.Weight.Should().Be(Default.WeightFor(section.Section));
        }
    }

    // THE enum-to-column switch, and the assertion that it really covers every member rather than
    // silently reading zero for one of them.
    [Theory]
    [InlineData(ReadabilitySectionType.Completeness, 0.1)]
    [InlineData(ReadabilitySectionType.Contact, 0.2)]
    [InlineData(ReadabilitySectionType.Achievements, 0.3)]
    [InlineData(ReadabilitySectionType.Chronology, 0.4)]
    [InlineData(ReadabilitySectionType.AtsParseability, 0.5)]
    public void ScoreFor_reads_the_column_that_belongs_to_the_section(
        ReadabilitySectionType section, double expected)
    {
        Breakdown(0.1, 0.2, 0.3, 0.4, 0.5).ScoreFor(section).Should().Be(expected);
    }

    [Fact]
    public void ScoreFor_rejects_a_section_that_is_not_a_member()
    {
        var act = () => Breakdown().ScoreFor((ReadabilitySectionType)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // FINITE FIRST: `NaN < 0` and `NaN > 1` are both false, so a NaN score would pass the range check and
    // then poison WeightedTotal, the band and the whole response.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Create_rejects_a_non_finite_score(double score)
    {
        var act = () => Breakdown(completeness: score);

        act.Should().Throw<ArgumentException>().WithMessage("*finite*");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Create_rejects_a_score_outside_the_unit_interval(double score)
    {
        var act = () => Breakdown(chronology: score);

        act.Should().Throw<ArgumentException>().WithMessage("*between 0 and 1*");
    }

    [Fact]
    public void Create_rejects_a_null_weights_snapshot()
    {
        var act = () => ReadabilityBreakdown.Create(0.0, 0.0, 0.0, 0.0, 0.0, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

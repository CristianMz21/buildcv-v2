using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Readability;

public class ReadabilityRecommendationTests
{
    private static ReadabilityRecommendation Advice(string message = "Add a phone number.", double impact = 0.05) =>
        ReadabilityRecommendation.Create(
            ReadabilitySectionType.Contact,
            RecommendationPriority.Important,
            ReadabilityRecommendationKind.NoPhoneNumber,
            message,
            impact);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_message(string? message)
    {
        var act = () => Advice(message!);

        act.Should().Throw<InvalidRecommendationException>();
    }

    [Fact]
    public void Create_trims_the_message()
    {
        Advice("  Add a phone number.  ").Message.Should().Be("Add a phone number.");
    }

    // FINITE FIRST. Both range comparisons are false for NaN, so a NaN impact would sail through and be
    // persisted as advice worth an unknowable amount — and it would sort arbitrarily against every other
    // recommendation. Reachable, because Impact is a section weight times a delta and section weights
    // are produced by a division.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Create_rejects_a_non_finite_impact(double impact)
    {
        var act = () => Advice(impact: impact);

        act.Should().Throw<InvalidRecommendationException>().WithMessage("*finite*");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Create_rejects_an_impact_outside_the_unit_interval(double impact)
    {
        var act = () => Advice(impact: impact);

        act.Should().Throw<InvalidRecommendationException>().WithMessage("*between 0 and 1*");
    }

    // The two boundaries are legal: zero-impact advice is honest advice about a section that cannot move,
    // and a full 1.0 is what a single-section report's only gap is worth.
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Create_accepts_the_ends_of_the_unit_interval(double impact)
    {
        Advice(impact: impact).Impact.Should().Be(impact);
    }
}

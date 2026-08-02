using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

public class SectionScoreTests
{
    [Fact]
    public void Create_keeps_the_section_its_score_and_the_weight_it_was_counted_under()
    {
        var section = SectionScore.Create(SectionType.Experience, 0.8, 0.20);

        section.Section.Should().Be(SectionType.Experience);
        section.Score.Should().Be(0.8);
        section.Weight.Should().Be(0.20);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Create_rejects_a_score_outside_the_unit_interval(double score)
    {
        var act = () => SectionScore.Create(SectionType.Skills, score, 0.45);

        act.Should().Throw<ArgumentException>().WithParameterName("score");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Create_rejects_a_weight_outside_the_unit_interval(double weight)
    {
        var act = () => SectionScore.Create(SectionType.Skills, 0.5, weight);

        act.Should().Throw<ArgumentException>().WithParameterName("weight");
    }

    // Both endpoints are legitimate: a section can score zero, and a section can carry zero weight —
    // which is exactly what Languages does in this PR.
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Create_accepts_the_endpoints_of_the_unit_interval(double value)
    {
        SectionScore.Create(SectionType.Languages, value, value).Should().NotBeNull();
    }

    [Fact]
    public void Sections_with_the_same_values_are_equal()
    {
        SectionScore.Create(SectionType.Skills, 0.5, 0.45)
            .Should().Be(SectionScore.Create(SectionType.Skills, 0.5, 0.45));

        SectionScore.Create(SectionType.Skills, 0.5, 0.45)
            .Should().NotBe(SectionScore.Create(SectionType.Experience, 0.5, 0.45));
    }
}

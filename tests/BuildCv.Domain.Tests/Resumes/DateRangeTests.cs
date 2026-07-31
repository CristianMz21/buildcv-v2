using BuildCv.Domain.Common.ValueObjects;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Resumes;

public class DateRangeTests
{
    [Fact]
    public void DateRange_with_end_can_be_created()
    {
        var range = new DateRange(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));

        range.Start.Should().Be(DateOnly.Parse("2022-01-01"));
        range.End.Should().Be(DateOnly.Parse("2023-06-30"));
    }

    [Fact]
    public void DateRange_without_end_represents_current()
    {
        var range = new DateRange(DateOnly.Parse("2022-01-01"), null);

        range.Start.Should().Be(DateOnly.Parse("2022-01-01"));
        range.End.Should().BeNull();
        range.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void DateRange_duration_in_days_can_be_calculated()
    {
        var range = new DateRange(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));

        range.DurationInDays.Should().Be(545);
    }

    [Fact]
    public void DateRange_is_immutable()
    {
        var range1 = new DateRange(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));
        var range2 = range1 with { End = DateOnly.Parse("2024-12-31") };

        range1.End.Should().Be(DateOnly.Parse("2023-06-30"));
        range2.End.Should().Be(DateOnly.Parse("2024-12-31"));
    }

    [Fact]
    public void DateRange_equality_works()
    {
        var range1 = new DateRange(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));
        var range2 = new DateRange(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));

        range1.Should().Be(range2);
    }
}

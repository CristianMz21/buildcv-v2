using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Resumes;

public class DateRangeTests
{
    [Fact]
    public void DateRange_with_end_can_be_created()
    {
        var range = DateRange.Create(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));

        range.Start.Should().Be(DateOnly.Parse("2022-01-01"));
        range.End.Should().Be(DateOnly.Parse("2023-06-30"));
    }

    [Fact]
    public void DateRange_without_end_represents_current()
    {
        var range = DateRange.Create(DateOnly.Parse("2022-01-01"));

        range.Start.Should().Be(DateOnly.Parse("2022-01-01"));
        range.End.Should().BeNull();
        range.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void DateRange_duration_in_days_can_be_calculated()
    {
        var range = DateRange.Create(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));

        range.DurationInDays(DateOnly.Parse("2023-06-30")).Should().Be(545);
    }

    [Fact]
    public void DateRange_without_end_uses_reference_date_for_duration()
    {
        var range = DateRange.Create(DateOnly.Parse("2022-01-01"));

        range.DurationInDays(DateOnly.Parse("2022-02-01")).Should().Be(31);
    }

    [Fact]
    public void DateRange_with_end_before_start_throws_invalid_date_range()
    {
        var act = () => DateRange.Create(
            DateOnly.Parse("2023-06-30"),
            DateOnly.Parse("2022-01-01"));

        act.Should().Throw<InvalidDateRangeException>();
    }

    [Fact]
    public void DateRange_is_immutable()
    {
        var range1 = DateRange.Create(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));
        var range2 = DateRange.Create(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));

        range1.Should().Be(range2);
        range2.End.Should().Be(DateOnly.Parse("2023-06-30"));
    }

    [Fact]
    public void DateRange_equality_works()
    {
        var range1 = DateRange.Create(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));
        var range2 = DateRange.Create(
            DateOnly.Parse("2022-01-01"),
            DateOnly.Parse("2023-06-30"));

        range1.Should().Be(range2);
    }
}

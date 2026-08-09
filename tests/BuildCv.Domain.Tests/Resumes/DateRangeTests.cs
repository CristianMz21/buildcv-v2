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

        range.StartsOn.Should().Be(DateOnly.Parse("2022-01-01"));
        range.EndsOn.Should().Be(DateOnly.Parse("2023-06-30"));
    }

    [Fact]
    public void DateRange_without_end_represents_current()
    {
        var range = DateRange.Create(DateOnly.Parse("2022-01-01"));

        range.StartsOn.Should().Be(DateOnly.Parse("2022-01-01"));
        range.End.Should().BeNull();
        range.EndsOn.Should().BeNull();
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
        range2.EndsOn.Should().Be(DateOnly.Parse("2023-06-30"));
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

    // ---------------------------------------------------------------- the duration convention
    //
    // FOUR CASES, ONE PER ENDPOINT AND PRECISION, and each pairs its partial end with a FULL one on the
    // other side. That is what makes them separately observable: a mutation to the month-start rule
    // cannot be masked by the month-end assertion, and neither touches the year rules. A single
    // month-to-month or year-to-year case would have been reddened by any of the four mutations and
    // could not have said which one. The expected day counts are literals, computed independently of
    // the implementation.

    [Fact]
    public void A_month_precision_start_counts_from_the_first_of_that_month()
    {
        var range = DateRange.Create(
            PartialDate.FromYearMonth(2015, 6),
            PartialDate.FromDate(new DateOnly(2019, 2, 20)));

        range.StartsOn.Should().Be(new DateOnly(2015, 6, 1));
        range.EndsOn.Should().Be(new DateOnly(2019, 2, 20));
        range.DurationInDays(new DateOnly(2026, 8, 9)).Should().Be(1360);
    }

    [Fact]
    public void A_month_precision_end_counts_to_the_last_day_of_that_month()
    {
        var range = DateRange.Create(
            PartialDate.FromDate(new DateOnly(2015, 6, 15)),
            PartialDate.FromYearMonth(2019, 2));

        range.StartsOn.Should().Be(new DateOnly(2015, 6, 15));
        range.EndsOn.Should().Be(new DateOnly(2019, 2, 28));
        range.DurationInDays(new DateOnly(2026, 8, 9)).Should().Be(1354);
    }

    [Fact]
    public void A_year_precision_start_counts_from_the_first_of_january()
    {
        var range = DateRange.Create(
            PartialDate.FromYear(2015),
            PartialDate.FromDate(new DateOnly(2019, 2, 20)));

        range.StartsOn.Should().Be(new DateOnly(2015, 1, 1));
        range.DurationInDays(new DateOnly(2026, 8, 9)).Should().Be(1511);
    }

    [Fact]
    public void A_year_precision_end_counts_to_the_thirty_first_of_december()
    {
        var range = DateRange.Create(
            PartialDate.FromDate(new DateOnly(2015, 6, 15)),
            PartialDate.FromYear(2019));

        range.EndsOn.Should().Be(new DateOnly(2019, 12, 31));
        range.DurationInDays(new DateOnly(2026, 8, 9)).Should().Be(1660);
    }

    // Both year rules at once, which is the shape a CV that says "2015 - 2019" actually produces. Kept
    // beside the two above rather than instead of them: this one alone could not say which end moved.
    [Fact]
    public void A_year_precision_range_spans_january_first_to_december_thirty_first()
    {
        var range = DateRange.Create(PartialDate.FromYear(2015), PartialDate.FromYear(2019));

        range.StartsOn.Should().Be(new DateOnly(2015, 1, 1));
        range.EndsOn.Should().Be(new DateOnly(2019, 12, 31));
        range.DurationInDays(new DateOnly(2026, 8, 9)).Should().Be(1825);
    }

    // The last day of a month is the calendar's answer, not a constant: February 2016 ends on the 29th.
    // A convention hard-coded to the 28th, or to 30, would answer this one wrong and the three above
    // right.
    [Fact]
    public void A_month_precision_end_in_a_leap_february_counts_to_the_twenty_ninth()
    {
        var range = DateRange.Create(
            PartialDate.FromYearMonth(2016, 2),
            PartialDate.FromYearMonth(2016, 2));

        range.EndsOn.Should().Be(new DateOnly(2016, 2, 29));
        range.DurationInDays(new DateOnly(2026, 8, 9)).Should().Be(28);
    }

    // An open-ended partial start still runs to the reference date, exactly as an open-ended full one
    // does: "Present" is data about when the question was asked, not about the precision of the answer.
    [Fact]
    public void An_open_ended_month_precision_range_runs_from_the_first_to_the_reference_date()
    {
        var range = DateRange.Create(PartialDate.FromYearMonth(2015, 6));

        range.IsCurrent.Should().BeTrue();
        range.DurationInDays(new DateOnly(2026, 8, 9)).Should().Be(4087);
    }

    // The ordering rule is about whether the period can run forwards under ANY reading its precision
    // allows, which is why an end stated more coarsely than the start is not automatically a violation.
    [Fact]
    public void A_range_that_could_run_forwards_under_its_stated_precision_is_accepted()
    {
        var act = () => DateRange.Create(
            PartialDate.FromDate(new DateOnly(2020, 3, 15)),
            PartialDate.FromYear(2020));

        act.Should().NotThrow();
    }

    [Fact]
    public void A_range_that_cannot_run_forwards_under_any_reading_is_refused()
    {
        var act = () => DateRange.Create(
            PartialDate.FromYearMonth(2020, 4),
            PartialDate.FromYearMonth(2020, 3));

        act.Should().Throw<InvalidDateRangeException>()
            .WithMessage("End date must be null or on/after start date.");
    }
}

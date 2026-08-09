using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Common;

public sealed class PartialDateTests
{
    [Fact]
    public void A_full_date_states_all_three_fields()
    {
        var date = PartialDate.FromDate(new DateOnly(2015, 6, 15));

        date.Year.Should().Be(2015);
        date.Month.Should().Be(6);
        date.Day.Should().Be(15);
    }

    // THE POINT OF THE TYPE: there is no day to read, so there is no day to misread. A sentinel-plus-flag
    // representation would answer 1 here and be believed.
    [Fact]
    public void A_month_precision_date_has_no_day_at_all()
    {
        var date = PartialDate.FromYearMonth(2015, 6);

        date.Year.Should().Be(2015);
        date.Month.Should().Be(6);
        date.Day.Should().BeNull();
    }

    [Fact]
    public void A_year_precision_date_has_neither_a_month_nor_a_day()
    {
        var date = PartialDate.FromYear(2015);

        date.Year.Should().Be(2015);
        date.Month.Should().BeNull();
        date.Day.Should().BeNull();
    }

    [Theory]
    [InlineData(2015, 6, "2015-06-01", "2015-06-30")]
    [InlineData(2016, 2, "2016-02-01", "2016-02-29")]
    [InlineData(2019, 2, "2019-02-01", "2019-02-28")]
    [InlineData(2020, 12, "2020-12-01", "2020-12-31")]
    public void A_month_precision_date_spans_that_whole_month(int year, int month, string earliest, string latest)
    {
        var date = PartialDate.FromYearMonth(year, month);

        date.EarliestDay.Should().Be(DateOnly.Parse(earliest));
        date.LatestDay.Should().Be(DateOnly.Parse(latest));
    }

    [Fact]
    public void A_year_precision_date_spans_that_whole_year()
    {
        var date = PartialDate.FromYear(2015);

        date.EarliestDay.Should().Be(new DateOnly(2015, 1, 1));
        date.LatestDay.Should().Be(new DateOnly(2015, 12, 31));
    }

    [Theory]
    [InlineData(2015, 0)]
    [InlineData(2015, 13)]
    [InlineData(2015, -1)]
    public void A_month_outside_the_calendar_is_refused(int year, int month)
    {
        var act = () => PartialDate.FromYearMonth(year, month);

        act.Should().Throw<InvalidPartialDateException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    [InlineData(-2015)]
    public void A_year_outside_what_a_calendar_date_can_hold_is_refused(int year)
    {
        var fromYear = () => PartialDate.FromYear(year);
        var fromYearMonth = () => PartialDate.FromYearMonth(year, 6);

        fromYear.Should().Throw<InvalidPartialDateException>();
        fromYearMonth.Should().Throw<InvalidPartialDateException>();
    }

    [Theory]
    [InlineData("2015-06-15", 2015, 6, 15)]
    [InlineData("2015-06", 2015, 6, null)]
    [InlineData("2015", 2015, null, null)]
    [InlineData("0001-01-01", 1, 1, 1)]
    [InlineData("9999-12-31", 9999, 12, 31)]
    [InlineData("2016-02-29", 2016, 2, 29)]
    public void The_three_written_forms_parse_to_the_three_precisions(
        string text, int year, int? month, int? day)
    {
        PartialDate.TryParse(text, out var date).Should().BeTrue();

        date!.Year.Should().Be(year);
        date.Month.Should().Be(month);
        date.Day.Should().Be(day);
        date.ToIsoString().Should().Be(text);
    }

    // Width is what carries the precision, so anything that is not exactly one of the three widths -- or
    // that pads, signs or spaces its way into one -- has to be refused rather than coerced. "2015-6"
    // matters most: accepted, it would be a second spelling of a value that is only ever written one way.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2015-6")]
    [InlineData("2015-06-1")]
    [InlineData("15-06-15")]
    [InlineData("2015-13")]
    [InlineData("2015-00")]
    [InlineData("2015-02-30")]
    [InlineData("2019-02-29")]
    [InlineData("0000")]
    [InlineData(" 015")]
    [InlineData("015 ")]
    [InlineData("+015")]
    [InlineData("2015/06")]
    [InlineData("2015-06-15T00:00:00")]
    [InlineData("June 2015")]
    public void Anything_that_is_not_one_of_the_three_forms_is_refused(string? text)
    {
        PartialDate.TryParse(text, out var date).Should().BeFalse();

        date.Should().BeNull();
    }

    [Fact]
    public void Two_values_stating_the_same_thing_are_equal_and_two_precisions_of_it_are_not()
    {
        PartialDate.FromYearMonth(2015, 6).Should().Be(PartialDate.FromYearMonth(2015, 6));

        // The distinction the whole change rests on: "June 2015" is not the 1st of June 2015.
        PartialDate.FromYearMonth(2015, 6).Should().NotBe(PartialDate.FromDate(new DateOnly(2015, 6, 1)));
        PartialDate.FromYear(2015).Should().NotBe(PartialDate.FromYearMonth(2015, 1));
    }
}

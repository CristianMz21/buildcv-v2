using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Resumes;

public class DateRangeTests
{
    [Fact]
    public void DateRange_with_end_can_be_created()
    {
        var range = new DateRange(Start: "2022-01", End: "2023-06");

        range.Start.Should().Be("2022-01");
        range.End.Should().Be("2023-06");
    }

    [Fact]
    public void DateRange_without_end_represents_current()
    {
        var range = new DateRange(Start: "2022-01", End: null);

        range.Start.Should().Be("2022-01");
        range.End.Should().BeNull();
    }

    [Fact]
    public void DateRange_is_immutable()
    {
        var range1 = new DateRange(Start: "2022-01", End: "2023-06");
        var range2 = range1 with { End = "2024-12" };

        range1.End.Should().Be("2023-06");
        range2.End.Should().Be("2024-12");
    }

    [Fact]
    public void DateRange_equality_works()
    {
        var range1 = new DateRange(Start: "2022-01", End: "2023-06");
        var range2 = new DateRange(Start: "2022-01", End: "2023-06");

        range1.Should().Be(range2);
    }
}

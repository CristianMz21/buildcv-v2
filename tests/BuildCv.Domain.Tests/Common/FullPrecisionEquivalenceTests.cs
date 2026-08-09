using System.Globalization;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Common;

// THE EVIDENCE THAT PARTIAL PRECISION IS ADDITIVE. Every date this repository could hold before
// PartialDate existed was a full one, so the whole safety argument for the change is a single claim:
// a DateRange built from two real days answers exactly what it answered before, for every question
// anything asks it.
//
// The claim is EXECUTED against an oracle rather than reasoned about. BeforePartialPrecision below is a
// verbatim copy of the pre-change DateRange semantics -- the end-before-start rule and the duration
// arithmetic, as they were written -- and it is deliberately NOT shared with production code, because an
// oracle that calls the implementation agrees with it whatever it does. Restating the old rule is what
// makes it an oracle.
//
// It is a sweep and not a table for the reason M2's empty-lexicon equivalence was: a table is a list of
// the cases someone thought of, and the interesting failures here are at boundaries -- month ends, leap
// days, the turn of a year, the ends of DateOnly's own range -- which is what the pool is built out of.
public sealed class FullPrecisionEquivalenceTests
{
    // Boundaries, not decoration: the last day of a 31-day and a 30-day month, both February lengths,
    // both ends of a year, and the two dates DateOnly itself cannot go outside. A convention that
    // widened a full date would show up here first.
    private static readonly DateOnly[] Pool =
    [
        DateOnly.MinValue,
        new(1900, 1, 1),
        new(1999, 12, 31),
        new(2000, 1, 1),
        new(2000, 2, 29),
        new(2000, 3, 1),
        new(2015, 6, 1),
        new(2015, 6, 15),
        new(2015, 6, 30),
        new(2016, 2, 29),
        new(2019, 2, 28),
        new(2019, 3, 1),
        new(2020, 1, 15),
        new(2020, 4, 30),
        new(2020, 12, 31),
        new(2021, 1, 1),
        new(2023, 7, 1),
        new(2024, 2, 29),
        new(2100, 12, 31),
        DateOnly.MaxValue,
    ];

    private static readonly DateOnly[] ReferenceDates =
    [
        new(1990, 1, 1),
        new(2020, 6, 15),
        new(2026, 8, 9),
        DateOnly.MaxValue,
    ];

    // A verbatim copy of DateRange as it stood before PartialDate. Do not point it at the real type.
    private static class BeforePartialPrecision
    {
        public static bool IsAcceptable(DateOnly start, DateOnly? end) => !(end.HasValue && end.Value < start);

        public static int DurationInDays(DateOnly start, DateOnly? end, DateOnly referenceDate) =>
            (end ?? referenceDate).DayNumber - start.DayNumber;
    }

    [Fact]
    public void EveryFullPrecisionRange_AnswersExactlyWhatItAnsweredBeforePartialPrecisionExisted()
    {
        foreach (var start in Pool)
        {
            foreach (var end in Pool.Select(day => (DateOnly?)day).Append(null))
            {
                var expectedAcceptable = BeforePartialPrecision.IsAcceptable(start, end);

                var create = () => DateRange.Create(start, end);
                if (!expectedAcceptable)
                {
                    create.Should().Throw<InvalidDateRangeException>(
                        "the old rule refused {0}..{1} and nothing about precision changes that", start, end);
                    continue;
                }

                var range = create();

                // The two DateOnly views collapse onto the stated days: a full date is not widened.
                range.StartsOn.Should().Be(start);
                range.EndsOn.Should().Be(end);
                range.IsCurrent.Should().Be(end is null);

                foreach (var referenceDate in ReferenceDates)
                {
                    range.DurationInDays(referenceDate).Should().Be(
                        BeforePartialPrecision.DurationInDays(start, end, referenceDate),
                        "the duration of {0}..{1} at {2} is arithmetic, not a convention", start, end, referenceDate);
                }
            }
        }
    }

    // A sweep whose every case landed on one side of the branch would be satisfied by a constant, so
    // both sides are shown to occur -- and shown to occur in quantity, since one accidental pair either
    // way is not coverage of a boundary.
    [Fact]
    public void TheEquivalenceSweep_ExercisesBothTheAcceptedAndTheRefusedAnswer()
    {
        var accepted = 0;
        var refused = 0;

        foreach (var start in Pool)
        {
            foreach (var end in Pool)
            {
                if (BeforePartialPrecision.IsAcceptable(start, end))
                    accepted++;
                else
                    refused++;
            }
        }

        accepted.Should().BeGreaterThan(Pool.Length,
            "a sweep that only ever accepted would be satisfied by a Create that never throws");
        refused.Should().BeGreaterThan(Pool.Length,
            "a sweep that only ever refused would be satisfied by a Create that always throws");
    }

    // The other half of "a full date is not widened": PartialDate itself. EarliestDay and LatestDay are
    // where the duration convention reads from, so if either moved for a stated day, every score built on
    // it would move with it.
    [Fact]
    public void EveryDateOnly_ConvertsToAFullPrecisionValueThatMeansThatDayAndNoOther()
    {
        var checkedDays = 0;
        for (var day = new DateOnly(1998, 1, 1); day <= new DateOnly(2005, 12, 31); day = day.AddDays(1))
        {
            var date = PartialDate.FromDate(day);

            date.Year.Should().Be(day.Year);
            date.Month.Should().Be(day.Month);
            date.Day.Should().Be(day.Day);
            date.EarliestDay.Should().Be(day);
            date.LatestDay.Should().Be(day);
            checkedDays++;
        }

        checkedDays.Should().Be(2922, "a loop that stopped early would prove nothing about the boundaries in it");

        foreach (var day in Pool)
        {
            PartialDate.FromDate(day).EarliestDay.Should().Be(day);
            PartialDate.FromDate(day).LatestDay.Should().Be(day);
        }
    }

    // The wire and the column both carry this text, and a full date's text is the one thing in this
    // change that a client and an existing row can both already read. It must not move by a character.
    [Fact]
    public void AFullPrecisionValue_StillWritesTheExactTenCharacterFormItAlwaysWrote()
    {
        foreach (var day in Pool)
        {
            var text = PartialDate.FromDate(day).ToIsoString();

            text.Should().Be(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            text.Should().HaveLength(10);

            PartialDate.TryParse(text, out var parsed).Should().BeTrue();
            parsed.Should().Be(PartialDate.FromDate(day));
        }
    }
}

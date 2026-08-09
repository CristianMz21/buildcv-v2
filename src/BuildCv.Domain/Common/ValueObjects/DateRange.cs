using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

/// <summary>
/// A period whose ends carry whatever precision their source stated — <c>"June 2015 – February 2019"</c>
/// is as expressible here as <c>15/06/2015 – 20/02/2019</c>.
/// </summary>
/// <remarks>
/// <para>
/// THE DURATION CONVENTION: <b>the longest interval the stated precision allows</b>. A month-precision
/// start counts from the 1st, a month-precision end counts to the last day of that month; a year counts
/// from January 1st to December 31st. <see cref="StartsOn"/> and <see cref="EndsOn"/> are that
/// convention, expressed once, and every reader of a duration goes through them.
/// </para>
/// <para>
/// THE ERROR IT COSTS, MEASURED. A month-precision endpoint can be wrong by up to 30 days (the real day
/// could be the last of a 31-day month while this counts from the first), and a year-precision endpoint
/// by up to 364. Against <c>ScoringRules.ExperienceDaysCap</c> — five years, 1825 days — 30 days is
/// 1.6% of the experience section, so a range with both ends at month precision moves that section by at
/// most 3.3%. Year precision is a different order: 364 days is 19.9% per endpoint, which is why the
/// extractor only ever produces it from a source that genuinely stated nothing else.
/// </para>
/// <para>
/// IT ROUNDS IN THE CANDIDATE'S FAVOUR, deliberately and in the same direction as every other judgement
/// call in this engine: when the document is unclear, offer rather than withhold. And the comparison
/// that matters is not against a perfect number — there is no perfect number to be had from a document
/// that never stated the day — but against the behaviour it replaces, where a month-precision date could
/// not be held at all, the field arrived empty, and the contribution was zero.
/// </para>
/// <para>
/// FULL PRECISION IS UNCHANGED, BIT FOR BIT. <see cref="PartialDate.EarliestDay"/> and
/// <see cref="PartialDate.LatestDay"/> are the same day for a date that states one, so
/// <see cref="StartsOn"/>, <see cref="EndsOn"/>, <see cref="DurationInDays"/> and the ordering rule in
/// <see cref="Create"/> all reduce to exactly what they did before partial precision existed. That is
/// what <c>FullPrecisionEquivalenceTests</c> executes rather than assumes.
/// </para>
/// </remarks>
public sealed record DateRange
{
    public PartialDate Start { get; }
    public PartialDate? End { get; }
    public bool IsCurrent => End is null;

    /// <summary>The first day this period covers, under the convention on this type.</summary>
    public DateOnly StartsOn => Start.EarliestDay;

    /// <summary>The last day this period covers, or null while it is still current.</summary>
    public DateOnly? EndsOn => End?.LatestDay;

    private DateRange(PartialDate start, PartialDate? end)
    {
        Start = start;
        End = end;
    }

    /// <summary>
    /// Rejects a period that ends before it starts under EVERY reading its precision allows.
    /// </summary>
    /// <remarks>
    /// The comparison is the end's LATEST day against the start's EARLIEST, so a period is refused only
    /// when no interpretation of the two makes it run forwards. Anything stricter would refuse ranges
    /// that are merely imprecise — "2020 – March 2020" states nothing contradictory — and at full
    /// precision, where both days collapse to one, it is the same <c>end &lt; start</c> test this type
    /// has always applied, with the same message.
    /// </remarks>
    public static DateRange Create(PartialDate start, PartialDate? end = null)
    {
        ArgumentNullException.ThrowIfNull(start);

        if (end is not null && end.LatestDay < start.EarliestDay)
            throw new InvalidDateRangeException("End date must be null or on/after start date.");

        return new DateRange(start, end);
    }

    /// <summary>
    /// The full-precision overload, kept so every caller that already has real days reads the same as
    /// it did — and so the diff on those call sites is nothing at all, which is its own evidence that
    /// their behaviour did not move.
    /// </summary>
    public static DateRange Create(DateOnly start, DateOnly? end = null) =>
        Create(PartialDate.FromDate(start), end is { } endDate ? PartialDate.FromDate(endDate) : null);

    public int DurationInDays(DateOnly referenceDate) =>
        (EndsOn ?? referenceDate).DayNumber - StartsOn.DayNumber;
}

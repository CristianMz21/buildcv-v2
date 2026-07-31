using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

public sealed record DateRange
{
    public DateOnly Start { get; }
    public DateOnly? End { get; }
    public bool IsCurrent => End is null;

    private DateRange(DateOnly start, DateOnly? end)
    {
        Start = start;
        End = end;
    }

    public static DateRange Create(DateOnly start, DateOnly? end = null)
    {
        if (end.HasValue && end.Value < start)
            throw new InvalidDateRangeException("End date must be null or on/after start date.");
        return new DateRange(start, end);
    }

    public int DurationInDays(DateOnly referenceDate) =>
        (End ?? referenceDate).DayNumber - Start.DayNumber;
}

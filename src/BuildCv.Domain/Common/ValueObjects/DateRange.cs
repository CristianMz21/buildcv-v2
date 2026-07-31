namespace BuildCv.Domain.Common.ValueObjects;

public sealed record DateRange(DateOnly Start, DateOnly? End)
{
    public bool IsCurrent => End is null;

    public int DurationInDays =>
        (End ?? DateOnly.FromDateTime(DateTime.Today)).DayNumber - Start.DayNumber;
}

namespace BuildCv.Domain.Readability;

public sealed record ReadabilityReportId
{
    public Guid Value { get; }

    public ReadabilityReportId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("ReadabilityReportId must not be empty.", nameof(value));
        Value = value;
    }

    public static ReadabilityReportId New() => new(Guid.NewGuid());
}

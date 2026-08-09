using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Readability;

public sealed record ReadabilityReportId
{
    public Guid Value { get; }

    public ReadabilityReportId(Guid value)
    {
        if (value == Guid.Empty)
            throw new EmptyIdentifierException("ReadabilityReportId must not be empty.");
        Value = value;
    }

    public static ReadabilityReportId New() => new(Guid.NewGuid());
}

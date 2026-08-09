using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Scoring;

public sealed record AnalysisId
{
    public Guid Value { get; }

    public AnalysisId(Guid value)
    {
        if (value == Guid.Empty)
            throw new EmptyIdentifierException("AnalysisId must not be empty.");
        Value = value;
    }

    public static AnalysisId New() => new(Guid.NewGuid());
}

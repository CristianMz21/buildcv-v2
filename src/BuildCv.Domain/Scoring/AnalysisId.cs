namespace BuildCv.Domain.Scoring;

public sealed record AnalysisId
{
    public Guid Value { get; }

    public AnalysisId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("AnalysisId must not be empty.", nameof(value));
        Value = value;
    }

    public static AnalysisId New() => new(Guid.NewGuid());
}

namespace BuildCv.Domain.Jobs;

public sealed record JobPostingId
{
    public Guid Value { get; }

    public JobPostingId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("JobPostingId must not be empty.", nameof(value));
        Value = value;
    }

    public static JobPostingId New() => new(Guid.NewGuid());
}

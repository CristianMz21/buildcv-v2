namespace BuildCv.Domain.Resumes;

public sealed record ResumeId
{
    public Guid Value { get; }

    public ResumeId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("ResumeId must not be empty.", nameof(value));
        Value = value;
    }

    public static ResumeId New() => new(Guid.NewGuid());
}

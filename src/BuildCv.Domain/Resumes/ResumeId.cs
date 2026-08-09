using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Resumes;

public sealed record ResumeId
{
    public Guid Value { get; }

    public ResumeId(Guid value)
    {
        if (value == Guid.Empty)
            throw new EmptyIdentifierException("ResumeId must not be empty.");
        Value = value;
    }

    public static ResumeId New() => new(Guid.NewGuid());
}

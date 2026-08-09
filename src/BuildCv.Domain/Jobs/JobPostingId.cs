using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Jobs;

public sealed record JobPostingId
{
    public Guid Value { get; }

    public JobPostingId(Guid value)
    {
        if (value == Guid.Empty)
            throw new EmptyIdentifierException("JobPostingId must not be empty.");
        Value = value;
    }

    public static JobPostingId New() => new(Guid.NewGuid());
}

using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Identity;

public sealed record AccountId
{
    public Guid Value { get; }

    public AccountId(Guid value)
    {
        if (value == Guid.Empty)
            throw new EmptyIdentifierException("AccountId must not be empty.");
        Value = value;
    }

    public static AccountId New() => new(Guid.NewGuid());
}

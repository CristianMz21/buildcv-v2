namespace BuildCv.Domain.Identity;

public sealed record AccountId
{
    public Guid Value { get; }

    public AccountId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("AccountId must not be empty.", nameof(value));
        Value = value;
    }

    public static AccountId New() => new(Guid.NewGuid());
}

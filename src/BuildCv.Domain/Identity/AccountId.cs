namespace BuildCv.Domain.Identity;

public sealed record AccountId(Guid Value)
{
    public static AccountId New() => new(Guid.NewGuid());
}

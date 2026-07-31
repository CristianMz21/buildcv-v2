namespace BuildCv.Domain.Organizations;

public sealed record OrganizationId
{
    public Guid Value { get; }

    public OrganizationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("OrganizationId must not be empty.", nameof(value));
        Value = value;
    }

    public static OrganizationId New() => new(Guid.NewGuid());
}

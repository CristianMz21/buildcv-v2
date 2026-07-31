namespace BuildCv.Domain.Organizations;

public sealed record OrganizationId(Guid Value)
{
    public static OrganizationId New() => new(Guid.NewGuid());
}

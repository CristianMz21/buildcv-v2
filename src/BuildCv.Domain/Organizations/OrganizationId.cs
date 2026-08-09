using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Organizations;

public sealed record OrganizationId
{
    public Guid Value { get; }

    public OrganizationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new EmptyIdentifierException("OrganizationId must not be empty.");
        Value = value;
    }

    public static OrganizationId New() => new(Guid.NewGuid());
}

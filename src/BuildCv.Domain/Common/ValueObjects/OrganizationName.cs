using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

public sealed record OrganizationName
{
    private const int MaxLength = 150;

    public string Value { get; }

    private OrganizationName(string value) => Value = value;

    public static OrganizationName Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > MaxLength)
            throw new InvalidOrganizationNameException($"Organization name exceeds {MaxLength} characters: {value}");

        return new OrganizationName(value.Trim());
    }

    public static bool TryCreate(string value, out OrganizationName? name)
    {
        try
        {
            name = Create(value);
            return true;
        }
        catch (Exception)
        {
            name = null;
            return false;
        }
    }

    public override string ToString() => Value;

    public static implicit operator string(OrganizationName name) => name.Value;
}

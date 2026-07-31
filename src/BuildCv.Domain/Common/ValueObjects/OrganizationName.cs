using System.Text;
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

        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Any(char.IsControl))
            throw new InvalidOrganizationNameException("Organization name must not contain control characters.");

        if (normalized.Length > MaxLength)
            throw new InvalidOrganizationNameException($"Organization name exceeds {MaxLength} characters.");

        return new OrganizationName(normalized);
    }

    public static bool TryCreate(string value, out OrganizationName? name)
    {
        try
        {
            name = Create(value);
            return true;
        }
        catch (DomainException)
        {
            name = null;
            return false;
        }
        catch (ArgumentException)
        {
            name = null;
            return false;
        }
    }

    public override string ToString() => Value;

    public static implicit operator string(OrganizationName name) => name.Value;
}

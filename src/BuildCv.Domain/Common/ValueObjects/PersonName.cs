using System.Text;
using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

public sealed record PersonName
{
    private const int MaxLength = 200;

    public string Value { get; }

    private PersonName(string value) => Value = value;

    public static PersonName Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Any(char.IsControl))
            throw new InvalidPersonNameException("Name must not contain control characters.");

        if (normalized.Length > MaxLength)
            throw new InvalidPersonNameException($"Name exceeds {MaxLength} characters.");

        return new PersonName(normalized);
    }

    public static bool TryCreate(string value, out PersonName? name)
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

    public static implicit operator string(PersonName name) => name.Value;
}

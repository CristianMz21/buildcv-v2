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

        if (value.Length > MaxLength)
            throw new InvalidPersonNameException($"Name exceeds {MaxLength} characters: {value}");

        return new PersonName(value.Trim());
    }

    public static bool TryCreate(string value, out PersonName? name)
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

    public static implicit operator string(PersonName name) => name.Value;
}

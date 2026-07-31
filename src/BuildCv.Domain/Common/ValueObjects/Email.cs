using System.Text.RegularExpressions;
using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

public sealed record Email
{
    private const int MaxLength = 254;
    private static readonly Regex Pattern = new(
        @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$",
        RegexOptions.Compiled);

    public string Value { get; }

    private Email(string value) => Value = value;

    public static Email Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > MaxLength)
            throw new InvalidEmailException($"Email exceeds {MaxLength} characters.");

        if (!Pattern.IsMatch(value))
            throw new InvalidEmailException("Invalid email format.");

        return new Email(value.ToLowerInvariant());
    }

    public static bool TryCreate(string value, out Email? email)
    {
        try
        {
            email = Create(value);
            return true;
        }
        catch (DomainException)
        {
            email = null;
            return false;
        }
        catch (ArgumentException)
        {
            email = null;
            return false;
        }
    }

    public override string ToString() => Value;

    public static implicit operator string(Email email) => email.Value;
}

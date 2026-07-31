using System.Text.RegularExpressions;
using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

public sealed record PhoneNumber
{
    private const int MaxLength = 20;
    private static readonly Regex Pattern = new(
        @"^\+[1-9]\d{6,14}$",
        RegexOptions.Compiled);

    public string Value { get; }

    private PhoneNumber(string value) => Value = value;

    public static PhoneNumber Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var digitsOnly = new string(value.Where(c => char.IsDigit(c) || c == '+').ToArray());

        if (digitsOnly.Length > MaxLength)
            throw new InvalidPhoneNumberException($"Phone number exceeds {MaxLength} characters.");

        if (!Pattern.IsMatch(digitsOnly))
            throw new InvalidPhoneNumberException("Invalid phone number format. Must start with + and have 7-15 digits.");

        return new PhoneNumber(digitsOnly);
    }

    public static bool TryCreate(string value, out PhoneNumber? phoneNumber)
    {
        try
        {
            phoneNumber = Create(value);
            return true;
        }
        catch (DomainException)
        {
            phoneNumber = null;
            return false;
        }
        catch (ArgumentException)
        {
            phoneNumber = null;
            return false;
        }
    }

    public override string ToString() => Value;

    public static implicit operator string(PhoneNumber phoneNumber) => phoneNumber.Value;
}

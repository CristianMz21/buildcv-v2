using System.Text.RegularExpressions;
using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

public sealed record Slug
{
    private static readonly Regex Pattern = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private const int MaxLength = 100;

    public string Value { get; }

    private Slug(string value) => Value = value;

    public static Slug Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim().ToLowerInvariant();

        if (trimmed.Length > MaxLength)
            throw new InvalidSlugException($"Slug exceeds {MaxLength} characters.");

        if (!Pattern.IsMatch(trimmed))
            throw new InvalidSlugException("Invalid slug format. Use lowercase letters, numbers, and hyphens.");

        return new Slug(trimmed);
    }

    public static bool TryCreate(string value, out Slug? slug)
    {
        try { slug = Create(value); return true; }
        catch (DomainException) { slug = null; return false; }
        catch (ArgumentException) { slug = null; return false; }
    }

    public override string ToString() => Value;
    public static implicit operator string(Slug slug) => slug.Value;
}

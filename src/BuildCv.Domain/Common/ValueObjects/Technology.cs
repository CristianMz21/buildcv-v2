using System.Text;
using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

public sealed record Technology
{
    private const int MaxLength = 100;

    public string Name { get; }

    private Technology(string name) => Name = name;

    public static Technology Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalized = name.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length > MaxLength)
            throw new InvalidTechnologyException($"Technology name exceeds {MaxLength} characters.");
        if (normalized.Any(char.IsControl))
            throw new InvalidTechnologyException("Technology name must not contain control characters.");

        return new Technology(normalized);
    }

    public static bool TryCreate(string name, out Technology? technology)
    {
        try { technology = Create(name); return true; }
        catch (DomainException) { technology = null; return false; }
        catch (ArgumentException) { technology = null; return false; }
    }

    public override string ToString() => Name;
}

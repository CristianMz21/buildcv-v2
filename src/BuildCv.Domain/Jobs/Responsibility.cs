using System.Text;
using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Jobs;

public sealed record Responsibility
{
    private const int MaxLength = 500;

    public string Description { get; }

    private Responsibility(string description) => Description = description;

    public static Responsibility Create(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var normalized = description.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Any(char.IsControl))
            throw new InvalidJobPostingException("Responsibility must not contain control characters.");
        if (normalized.Length > MaxLength)
            throw new InvalidJobPostingException($"Responsibility exceeds {MaxLength} characters.");
        return new Responsibility(normalized);
    }
}

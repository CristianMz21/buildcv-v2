using System.Text;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Jobs;

// What a posting ASKS FOR in a language, as opposed to Resumes.Language, which is what a candidate
// HAS. The two deliberately do not share a type: a reference from Jobs into Resumes would be the
// first cross-context type reference in the Domain, and it would point the wrong way besides — a
// posting's requirement does not depend on anyone's resume.
//
// The nicer long-term shape is a shared LanguageName value object in Common/ValueObjects mirroring
// Technology, which both sides would then normalize through. That is a Resumes-context change and is
// left as a follow-up rather than smuggled into a Jobs-context PR.
//
// Normalization mirrors Technology.Create, so the two vocabularies of the Jobs context are cleaned
// the same way and a posting reads back exactly as it was written.
public sealed record LanguageRequirement
{
    // Public because JobPostingConfiguration maps the column with it. Two independent 100s that have to
    // agree is a truncation waiting to happen: widen one and EF silently cuts the value at the other.
    public const int MaxNameLength = 100;

    public string Name { get; }
    public LanguageProficiency MinimumLevel { get; }

    private LanguageRequirement(string name, LanguageProficiency minimumLevel)
    {
        Name = name;
        MinimumLevel = minimumLevel;
    }

    public static LanguageRequirement Create(string name, LanguageProficiency minimumLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalized = name.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length > MaxNameLength)
            throw new InvalidJobPostingException($"Language name exceeds {MaxNameLength} characters.");
        if (normalized.Any(char.IsControl))
            throw new InvalidJobPostingException("Language name must not contain control characters.");

        return new LanguageRequirement(normalized, minimumLevel);
    }
}

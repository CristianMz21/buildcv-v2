using System.Text;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Resumes;

// The one child record on this aggregate that carries a rule of its own, and the reason is that Name is
// the only PLAINTEXT BOUNDED column in the whole resume graph: ResumeConfiguration maps it
// `HasMaxLength(100)` because the scoring engine joins on it, while every other free-text column is an
// encrypted varbinary(max) that cannot overflow.
//
// Without this rule a 101-character name reached SQL Server and came back as error 2628, "String or
// binary data would be truncated" — which SaveChangesExtensions does not translate (it knows 2601 and
// 2627), so it escaped as a 500 AND GlobalExceptionHandler logged the exception chain, putting the
// candidate's own text into the application log. The rule belongs here rather than in a caller: the
// import path validates BY CONSTRUCTING, so stating it once in the Domain is what stops the persistence
// layer from becoming a third opinion about validity.
//
// Private constructor + static factory, matching Skill on this same aggregate. EF materializes through
// the private constructor and therefore does NOT re-run Create, so rows written before this rule
// existed still load.
public sealed record Language
{
    private const int MaxNameLength = 100;

    public string Name { get; }
    public string? Fluency { get; init; }

    // The level the scorer reads. Fluency stays free text for display and MUST NEVER be parsed into
    // this, however tempting a normalization table looks.
    //
    // The reason is that the failure has a DIRECTION. A table maps the words it knows and drops the
    // rest, and an unrecognized word reads as "not proficient" rather than as "unknown" -- so a
    // candidate who wrote "Bilingüe", "Materna", "C2" or "Nativo (LATAM)" scores ZERO on Spanish. In a
    // product whose users are Spanish-speaking job seekers that is the worst answer the engine could
    // produce, and it would be produced most often for the people it is most wrong about.
    //
    // A missing Level is missing DATA, not a low level. PR 3 turns it into advice -- a recommendation
    // naming the fix -- instead of into a penalty. Nullable for exactly that reason.
    public LanguageProficiency? Level { get; init; }

    private Language(string name, string? fluency, LanguageProficiency? level)
    {
        Name = name;
        Fluency = fluency;
        Level = level;
    }

    public static Language Create(string name, string? fluency = null, LanguageProficiency? level = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalized = name.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Any(char.IsControl))
            throw new InvalidLanguageException("Language name must not contain control characters.");

        // Checked AFTER normalizing, because composing to Form C can change the length.
        if (normalized.Length > MaxNameLength)
            throw new InvalidLanguageException($"Language name exceeds {MaxNameLength} characters.");

        return new Language(normalized, fluency, level);
    }
}

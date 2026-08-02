using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Language(
    string Name,
    string? Fluency,

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
    LanguageProficiency? Level = null);

using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Education(
    OrganizationName Institution,
    string? Degree,
    string? FieldOfStudy,
    DateRange Period,
    string? Grade,

    // The comparable value the scorer reads. Degree stays free text for display, and the same rule as
    // Language.Level applies to it: do not parse it into this. "Ingeniero en Sistemas",
    // "Licenciatura" and "Técnico Superior" are not in any table someone will write by hand, and an
    // unrecognized degree falling through as "no level" costs the candidate the requirement instead
    // of flagging the gap.
    //
    // There is a second, harder reason nothing can derive one from the other: Degree is ENCRYPTED
    // (ResumeConfiguration) and Level is plaintext, so no query can reach through the envelope.
    //
    // Nullable, and it stays nullable: a resume that names no level is missing data, which PR 3 turns
    // into a recommendation rather than into a penalty.
    EducationLevel? Level = null);

namespace BuildCv.Domain.Readability;

// The five parts a resume's READABILITY is broken down into. A sibling of Scoring.SectionType, never an
// extension of it, and the reason is concrete rather than stylistic:
//
//   1. Scoring.ScoreBreakdown.Sections projects Enum.GetValues<SectionType>() and ScoreFor throws on a
//      member it has no column for, so that enum is effectively CLOSED at six for every persisted
//      breakdown -- appending to it breaks stored rows at READ time, not at write time.
//   2. The two enums answer questions about different subjects. A SectionType is a part of the MATCH
//      between a resume and one posting; a ReadabilitySectionType is a part of the resume ALONE, and a
//      readability report is taken with no posting in existence.
//
// Every member states its number because this is persisted as a tinyint. Letting the compiler assign
// them means inserting a member in the middle silently renumbers every member after it, and every row
// already on disk starts reading back as the wrong section -- a corruption with no error to notice.
public enum ReadabilitySectionType
{
    Completeness = 0,
    Contact = 1,
    Achievements = 2,
    Chronology = 3,
    AtsParseability = 4
}

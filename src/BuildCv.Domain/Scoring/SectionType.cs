namespace BuildCv.Domain.Scoring;

// The six parts of a resume a score is broken down into.
//
// Every member states its number because this is persisted as a tinyint. Letting the compiler assign
// them means inserting a member in the middle silently renumbers every member after it, and every row
// already on disk starts reading back as the wrong section — a corruption with no error to notice.
public enum SectionType
{
    Skills = 0,
    Experience = 1,
    Education = 2,
    Certifications = 3,
    Projects = 4,
    Languages = 5
}

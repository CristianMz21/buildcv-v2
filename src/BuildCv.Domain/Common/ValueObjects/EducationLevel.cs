namespace BuildCv.Domain.Common.ValueObjects;

// Shared vocabulary, for the same reason as LanguageProficiency: Resumes.Education.Level is what the
// candidate holds, JobPosting.EducationLevel is what the posting asks for, and PR 3 compares them.
//
// The ORDER is the contract. Members ascend, so `held >= required` is the whole comparison. A
// country-specific title (licenciatura, ingeniería, técnico superior) maps ONTO this ladder rather
// than adding a member to it — the ladder has to stay short enough to stay totally ordered.
//
// Persisted as tinyint on both sides, so the numbers are stated explicitly. Inserting a member in the
// middle renumbers every member after it and rewrites the meaning of every row already on disk.
public enum EducationLevel
{
    HighSchool = 0,
    Associate = 1,
    Bachelor = 2,
    Master = 3,
    Doctorate = 4
}

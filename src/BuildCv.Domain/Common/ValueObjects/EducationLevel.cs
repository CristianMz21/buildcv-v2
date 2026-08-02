namespace BuildCv.Domain.Common.ValueObjects;

// Shared vocabulary, for the same reason as LanguageProficiency: Resumes.Education.Level is what the
// candidate holds and JobPosting.EducationLevel is what the posting asks for.
//
// NOTHING COMPARES THEM YET. The scoring engine reads neither — Education is scored from whether the
// candidate recorded a degree at all, which is why it applies to every posting and is never
// renormalized out. Both columns are mapped, round-tripped and queryable, waiting for the rule that
// will use them; see the note on ScoringRules.AlwaysApplicable for what changes when it arrives.
//
// The ORDER is the contract, and it is what that future rule will lean on. Members ascend, so
// `held >= required` is the whole comparison — no lookup table, no ranking function to disagree. A
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

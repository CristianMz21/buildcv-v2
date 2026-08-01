namespace BuildCv.Domain.Common.ValueObjects;

// Shared vocabulary, which is why it lives here and not in Resumes or Jobs: both sides of a score
// speak it. Resumes.Language.Level is what the candidate has, Jobs.LanguageRequirement.MinimumLevel
// is what a posting asks for, and PR 3 compares the two.
//
// The ORDER is the contract. Members ascend from least to most proficient, so `held >= required` is
// the whole comparison — no lookup table, no ranking function that could disagree with the enum.
//
// Persisted as tinyint on both sides, so the numbers are stated rather than left to declaration
// order: inserting a member in the middle renumbers every member after it and every row already on
// disk starts reading back as something else. Append at the end, or renumber WITH a data migration.
public enum LanguageProficiency
{
    Basic = 0,
    Conversational = 1,
    Professional = 2,
    Fluent = 3,
    Native = 4
}

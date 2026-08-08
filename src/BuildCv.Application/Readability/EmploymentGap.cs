namespace BuildCv.Application.Readability;

using BuildCv.Domain.Resumes;

// One unexplained break in a candidate's work history: how long it ran, and the entry that follows it.
//
// `Following` is carried so the advice can name WHICH break it means -- a resume with three gaps gets
// three recommendations, and "add an entry covering the gap" is useless without saying which one. It is
// the entry, not a copy of its text, so nothing here duplicates resume content that would then have to
// be classified twice.
internal sealed record EmploymentGap(int Days, Experience Following)
{
    // Whole months, rounded DOWN, purely for the sentence. Nothing computes a score from this: the score
    // reads Days against ReadabilityRules.MaxGapDays, so a rounding choice here can never move a number.
    internal int Months => Days / 30;
}

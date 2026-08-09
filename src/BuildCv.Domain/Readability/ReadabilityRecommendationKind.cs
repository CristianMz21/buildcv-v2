namespace BuildCv.Domain.Readability;

// A stable name for WHICH READABILITY RULE FIRED, independent of the sentence that rule produces.
//
// It exists because ReadabilityRecommendation.Message is encrypted -- the sentence quotes the resume --
// so no query can group by it. Section and Priority alone are far too coarse to answer "which advice do
// we give most often, and does it help": a bullet point missing a number and one missing an action verb
// would collapse into the same bucket. This is the column that keeps that question answerable, which is
// why it is plaintext and why its numbers are explicit -- it is persisted as a tinyint.
//
// The members are the kinds the readability rules emit. Adding one is APPEND-ONLY; renumbering an
// existing member rewrites the meaning of every row already written under it.
public enum ReadabilityRecommendationKind
{
    NoEducationRecorded = 0,
    NoSkillsRecorded = 1,
    NoProfessionalSummary = 2,
    NoPhoneNumber = 3,
    NoLocation = 4,
    NoOnlinePresence = 5,
    NoExperienceRecorded = 6,
    NoExperienceHighlights = 7,
    HighlightWithoutANumber = 8,
    HighlightWithoutAnActionVerb = 9,
    UnexplainedEmploymentGap = 10,

    // The three ATS-parseability kinds. They are the only ones that name an edit to the DOCUMENT rather
    // than to the resume, and the only ones whose fix is "and import it again" -- see the remarks on
    // ImportSignals for why a product that never keeps the file cannot re-grade the old upload.
    //
    // The first two are two causes of one gap and are mutually exclusive by construction: a scanned PDF
    // and a document with no text in it both leave an ATS with nothing, and they are separate members
    // because the candidate's fix differs and because "which of these do we see most" is the question
    // this enum exists to keep answerable.
    DocumentHasNoTextLayer = 11,
    DocumentHasNoText = 12,
    DocumentUsesMultipleColumns = 13
}

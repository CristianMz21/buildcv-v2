namespace BuildCv.Domain.Scoring;

// A stable name for WHICH RULE FIRED, independent of the sentence that rule produces.
//
// It exists because Recommendation.Message is encrypted — the sentence quotes resume content — so no
// query can group by it. Section and Priority alone are far too coarse to answer "which advice do we
// give most often, and does it help": a missing must-have skill and a missing nice-to-have skill would
// collapse into the same bucket. This is the column that keeps that question answerable, which is why
// it is plaintext and why its numbers are explicit — it is persisted as a tinyint.
//
// The members are the kinds the scoring rules emit. Adding one is APPEND-ONLY; renumbering an existing
// member rewrites the meaning of every row already scored under it.
public enum RecommendationKind
{
    MissingMustHaveSkill = 0,
    MissingNiceToHaveSkill = 1,
    NoEducationRecorded = 2,
    NoDegreeRecorded = 3,
    FewerCertificationsThanExpected = 4,
    FewerProjectsThanExpected = 5,
    LanguageMissing = 6,
    LanguageBelowRequiredLevel = 7,
    LanguageLevelNotRecorded = 8,
    ExperienceNotMarkedProfessional = 9
}

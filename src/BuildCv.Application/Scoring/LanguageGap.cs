namespace BuildCv.Application.Scoring;

// Why one language requirement is not met, or that it is.
//
// Three distinct failures that a single bool would flatten into one, and they map one-to-one onto
// three RecommendationKind members because the advice differs in each case: add the language, raise
// the level, or record the level you already have. Only the third is about missing DATA rather than
// missing ability, and it is the one a candidate can usually close in seconds.
//
// Never persisted -- it exists for the length of one scoring pass -- so unlike the enums in
// Domain.Scoring its numbers are not a contract.
internal enum LanguageGap
{
    Satisfied,
    Missing,
    BelowRequiredLevel,
    LevelNotRecorded
}

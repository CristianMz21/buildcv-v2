namespace BuildCv.Api.Contracts;

using BuildCv.Domain.Scoring;

public sealed record ScoreResumeRequest(Guid ResumeId, Guid JobPostingId);

// The wire shape of a scoring run. It exists because /scoring/score used to return the Analysis
// AGGREGATE, which CLAUDE.md forbids and which this release makes expensive: System.Text.Json
// serializes RecommendationKind and RecommendationPriority off the aggregate as RAW INTEGERS, and
// those numbers are documented in three files as an append-only PERSISTENCE detail. The moment a
// client binds to them they are a public API contract too, and renumbering becomes a breaking change.
// So the DTO lands in the same release that first emits a recommendation, not after it.
//
// TODAY'S RESPONSE IS REPRODUCED VERBATIM and only added to. Two consequences are deliberate and both
// look like inconsistencies:
//
//   - Ids stay wrapped as {"value": guid} and `band` stays an integer. Flipping either convention on
//     one endpoint out of five is worse than a consistent bad convention; that is its own repo-wide
//     change.
//   - `breakdown.sections[].section` stays an integer while `recommendations[].section` is the string
//     "Skills". Same enum, same response, two encodings. The sections projection is the shape this
//     endpoint already ships; the recommendations are new, and new fields get names because names are
//     what stop the numbering becoming a contract. A follow-up can register JsonStringEnumConverter
//     globally and flip `band` and `sections[].section` together, deliberately and in one place.
//
// Enum names are produced by ToString() here rather than by a serializer converter, so the wire
// contract is stated in this file and cannot be changed by an option added elsewhere.
public sealed record AnalysisResponse(
    IdEnvelope Id,
    ScoreBreakdownResponse Breakdown,
    IdEnvelope ResumeId,
    IdEnvelope JobPostingId,
    DateTimeOffset ScoredAt,
    IReadOnlyList<RecommendationResponse> Recommendations,
    int OverallScore,
    int Band)
{
    public static AnalysisResponse From(Analysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        return new AnalysisResponse(
            new IdEnvelope(analysis.Id.Value),
            ScoreBreakdownResponse.From(analysis.Breakdown),
            new IdEnvelope(analysis.ResumeId.Value),
            new IdEnvelope(analysis.JobPostingId.Value),
            analysis.ScoredAt,
            // Sorted HERE, and not merely trusted. Analysis.Recommendations is a set: the child table
            // carries a surrogate key and no stored position, so a reloaded analysis hands them back in
            // whatever order the server chose. The Application layer sorts before persisting, to decide
            // which ten survive the cap; this sorts again, to decide what the candidate reads first.
            [.. RecommendationOrder.Sort(analysis.Recommendations).Select(RecommendationResponse.From)],
            analysis.OverallScore,
            (int)analysis.Band);
    }
}

// The {"value": guid} wrapper every id on this endpoint already ships in. Declared here rather than
// left to the strongly-typed id's own serialization, so the wire shape is this file's decision and a
// Domain refactor cannot change it by accident.
public sealed record IdEnvelope(Guid Value);

public sealed record ScoreBreakdownResponse(
    double SkillsScore,
    double ExperienceScore,
    double EducationScore,
    double CertificationsScore,
    double ProjectsScore,
    double LanguagesScore,
    ScoringWeightsResponse Weights,
    double WeightedTotal,
    IReadOnlyList<SectionScoreResponse> Sections)
{
    public static ScoreBreakdownResponse From(ScoreBreakdown breakdown) =>
        new(
            breakdown.SkillsScore,
            breakdown.ExperienceScore,
            breakdown.EducationScore,
            breakdown.CertificationsScore,
            breakdown.ProjectsScore,
            breakdown.LanguagesScore,
            ScoringWeightsResponse.From(breakdown.Weights),
            breakdown.WeightedTotal,
            [.. breakdown.Sections.Select(SectionScoreResponse.From)]);
}

// Which weighting explained this score. SchemaVersion travels with it deliberately: a client comparing
// two analyses from either side of a redistribution needs to know the two are not measured in the same
// units.
public sealed record ScoringWeightsResponse(
    double Skills,
    double Experience,
    double Education,
    double Certifications,
    double Projects,
    double Languages,
    int SchemaVersion)
{
    public static ScoringWeightsResponse From(ScoringWeightsSnapshot weights) =>
        new(
            weights.Skills,
            weights.Experience,
            weights.Education,
            weights.Certifications,
            weights.Projects,
            weights.Languages,
            weights.SchemaVersion);
}

// `Section` is an int here and a string on RecommendationResponse. That is the shape this array already
// ships in, kept rather than corrected — see the note on AnalysisResponse.
public sealed record SectionScoreResponse(int Section, double Score, double Weight)
{
    public static SectionScoreResponse From(SectionScore section) =>
        new((int)section.Section, section.Score, section.Weight);
}

// Impact is on the same 0..1 scale as every score in this response, NOT the 0..100 scale OverallScore
// uses. A client showing it as points multiplies by 100, exactly as it would for a section score.
public sealed record RecommendationResponse(
    string Section,
    string Priority,
    string Kind,
    string Message,
    double Impact)
{
    public static RecommendationResponse From(Recommendation recommendation) =>
        new(
            recommendation.Section.ToString(),
            recommendation.Priority.ToString(),
            recommendation.Kind.ToString(),
            recommendation.Message,
            recommendation.Impact);
}

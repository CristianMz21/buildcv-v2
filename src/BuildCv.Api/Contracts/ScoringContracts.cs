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
// THE PRE-CHAIN RESPONSE IS REPRODUCED VERBATIM. Everything this chain added carries enum NAMES; only
// the fields that predate it keep their old encoding.
//
//   - Ids stay wrapped as {"value": guid} and `band` stays an integer. Both predate this chain and have
//     clients. Flipping either convention on one endpoint out of five is worse than a consistent bad
//     convention; that is its own repo-wide change.
//   - `breakdown.sections[]` and `recommendations[]` BOTH name their sections: "Skills", not 0. The
//     sections projection looks pre-existing but is not — it was added by PR 1, is unmerged and has no
//     clients, so it is this chain's shape rather than the endpoint's. Shipping a raw enum integer
//     inside the very DTO that exists to stop enum numbers becoming a public contract is the one
//     inconsistency that gets harder to justify with every release, so it was corrected while it was
//     still free.
//
// Every encoding here is decided in this file: enum names come from ToString() and `band` from an int
// property, so a JsonStringEnumConverter registered globally later cannot silently change any of it.
// That makes the response converter-proof, which is the property that matters — not the numbering.
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

// `Weight` is what tells a client whether this section was ASKED ABOUT AT ALL. A weight of 0.0 means
// the posting stated no requirement for it, so the score beside it measures nothing and should not be
// rendered as a result — that pairing is the whole reason SectionScore carries both numbers together,
// and it is the only signal a client needs to explain "why is this section not counted".
public sealed record SectionScoreResponse(string Section, double Score, double Weight)
{
    public static SectionScoreResponse From(SectionScore section) =>
        new(section.Section.ToString(), section.Score, section.Weight);
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

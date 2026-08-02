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
/// <summary>
/// One scoring run, as returned by <c>POST /scoring/score</c>, <c>GET /scoring/{analysisId}</c> and
/// each entry of <c>GET /resumes/{id}/analyses</c>. One shape for one aggregate, on purpose.
/// </summary>
/// <remarks>
/// <para>
/// <b>A section that expressed no weighted requirement carries a weight of 0</b> — in
/// <c>Breakdown.Weights</c> and beside its score in <c>Breakdown.Sections</c>. It neither helped nor
/// hurt this score, and the score printed next to it measures nothing. There is deliberately NO
/// separate "applicable" flag: the weight IS the signal, so the two can never disagree about the same
/// fact.
/// </para>
/// <para>
/// <b><c>Weights.Skills</c> and <c>Weights.Languages</c> are 0 on every analysis this build can
/// produce</b>, and that is a missing feature rather than anything a recruiter chose. No endpoint puts
/// a skill or language requirement on a posting: <c>POST /jobs</c> carries only a title, company and
/// description, and there is no update endpoint. A UI that renders those two zeros as "this job listed
/// no skill requirements" would say it about every job in the product.
/// </para>
/// <para>
/// The remaining weights are RENORMALIZED to still total 1.0, so the ceiling is 100 for every posting.
/// That is why an <see cref="OverallScore"/> of 0 accompanied by only three recommendations is a
/// complete answer and not a truncated one: the sections that were asked about all scored zero, and
/// the ones that were not are absent from both the total and the advice.
/// </para>
/// <para>
/// Two analyses can therefore report the same <c>Weights.SchemaVersion</c> and still have been scored
/// under different weightings, because each posting asks about a different set of sections. The
/// version names the weighting RULE; the snapshot names the RESULT. Compare the weights before
/// comparing two <see cref="OverallScore"/> values across postings.
/// </para>
/// </remarks>
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

/// <summary>
/// One section's score paired with the weight it carried in this analysis. The pairing is the whole
/// reason the type exists: neither number explains the total on its own.
/// </summary>
/// <param name="Section">The section name — <c>"Skills"</c>, never <c>0</c>.</param>
/// <param name="Score">
/// How well the resume matched this section, 0..1. <b>Meaningless when <paramref name="Weight"/> is
/// 0</b>: nothing was asked, so nothing was measured, and a client should not render it as a result.
/// </param>
/// <param name="Weight">
/// The share of the overall score this section carried, 0..1, after renormalization. <b>0 means the
/// posting expressed no weighted requirement for this section</b>, so it neither helped nor hurt —
/// this is the only signal a client needs to explain "why is this section not counted", and there is
/// deliberately no parallel flag saying the same thing a second time. The weights across all six
/// sections total 1.0, the zero-weighted ones having been redistributed proportionally over the rest.
/// <para>
/// "Expressed no weighted requirement" is not the same claim as "stated no requirement", and the
/// difference is reachable: a posting may state requirements and weight every one of them 0.0, which
/// renormalizes the section out while <c>Recommendations</c> still names those requirements with an
/// <c>Impact</c> of 0. So a weight of 0 does not license a client to say the posting asked nothing —
/// only that nothing it asked could move the total.
/// </para>
/// </param>
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

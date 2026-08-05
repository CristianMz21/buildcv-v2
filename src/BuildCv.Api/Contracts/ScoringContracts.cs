namespace BuildCv.Api.Contracts;

using BuildCv.Domain.Scoring;

public sealed record ScoreResumeRequest(Guid ResumeId, Guid JobPostingId);

// The wire shape of a scoring run. It exists because /scoring/score used to return the Analysis
// AGGREGATE, which CLAUDE.md forbids: System.Text.Json serialized RecommendationKind and
// RecommendationPriority off the aggregate as RAW INTEGERS, and those numbers are documented in three
// files as an append-only PERSISTENCE detail. The moment a client binds to them they are a public API
// contract too, and renumbering becomes a breaking change. A DTO is where every wire encoding is a
// decision rather than a serialization accident.
//
// EVERY ENUM IN THIS RESPONSE CARRIES ITS NAME, AND EVERY ID IS A BARE GUID. The v1 release settled
// the two encodings this file used to carry as documented debts:
//
//   - `band` shipped as an integer while every other enum in the same response carried its name. It
//     is the ScoreBand name now ("Good", never 2), so renumbering the enum stays a persistence
//     concern instead of a client break.
//   - Ids shipped wrapped as {"value": guid} — the shape a strongly-typed id record happens to
//     serialize into. They are bare guids now, unwrapped in the same release here and in
//     JobContracts.cs. THE RESUME ROUTES ARE NOT SETTLED YET and still answer with the Resume
//     AGGREGATE, so their ids remain enveloped and their level enums remain integers; that is the
//     next commit, and until it lands "v1 ids are bare" is a claim about this file and /jobs only.
//
// Both flips happened in the release that introduced /v1 BECAUSE it was that release: no frontend or
// third-party client existed yet, so shipping the correct shape was free, exactly once. The moment a
// client binds to v1, either change becomes a /v2.
//
// `breakdown.sections[]` and `recommendations[]` both name their sections: "Skills", not 0. Every
// encoding here is decided in this file — enum names come from ToString() — so a
// JsonStringEnumConverter registered globally later cannot silently change any of it. That makes the
// response converter-proof, which is the property that matters, not the numbering.
/// <summary>
/// One scoring run, as returned by <c>POST /v1/scoring/score</c>, <c>GET /v1/scoring/{analysisId}</c>
/// and each entry of <c>GET /v1/resumes/{id}/analyses</c>. One shape for one aggregate, on purpose.
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
/// <b><c>Weights.Languages</c> is 0 on every analysis this build can produce</b>, and that is a missing
/// feature rather than anything a recruiter chose: no endpoint puts a language requirement on a posting.
/// <b><c>Weights.Skills</c> is no longer always 0</b> — <c>POST /v1/job-offers/import</c> lets a
/// candidate state skill requirements on their own Draft offer, so an analysis scored against an
/// imported offer carries a NONZERO skills weight. It is still 0 for a posting created through
/// <c>POST /v1/jobs</c>, which carries only a title, company and description. A UI must read the weight
/// per analysis, not assume either is always 0.
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
    Guid Id,
    ScoreBreakdownResponse Breakdown,
    Guid ResumeId,
    Guid JobPostingId,
    DateTimeOffset ScoredAt,
    IReadOnlyList<RecommendationResponse> Recommendations,
    int OverallScore,
    string Band)
{
    public static AnalysisResponse From(Analysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        return new AnalysisResponse(
            analysis.Id.Value,
            ScoreBreakdownResponse.From(analysis.Breakdown),
            analysis.ResumeId.Value,
            analysis.JobPostingId.Value,
            analysis.ScoredAt,
            // Sorted HERE, and not merely trusted. Analysis.Recommendations is a set: the child table
            // carries a surrogate key and no stored position, so a reloaded analysis hands them back in
            // whatever order the server chose. The Application layer sorts before persisting, to decide
            // which ten survive the cap; this sorts again, to decide what the candidate reads first.
            [.. RecommendationOrder.Sort(analysis.Recommendations).Select(RecommendationResponse.From)],
            analysis.OverallScore,
            analysis.Band.ToString());
    }
}

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

namespace BuildCv.Api.Contracts;

using BuildCv.Domain.Readability;

// The wire shape of a readability run. A separate file from ScoringContracts for the same reason
// ReadabilityReport is a separate aggregate from Analysis: the two answer different questions about
// different subjects, and one DTO carrying both would be the blended figure this milestone refuses to
// compute.
//
// EVERY ENUM IN THIS RESPONSE CARRIES ITS NAME, AND EVERY ID IS A BARE GUID, which is the v1 settlement
// stated in ScoringContracts and executed by V1ContractShapeTests over the real body of this route.
// Every encoding here is decided in this file — enum names come from ToString() — so a
// JsonStringEnumConverter registered globally later cannot silently change any of it.
/// <summary>
/// One readability run, as returned by <c>POST /v1/resumes/{id}/readability</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a match score, and the two must never be added together.</b> <see cref="ReadabilityScore"/>
/// is a fact about the RESUME — it needs no job posting and is available before the candidate has a
/// target job. <c>AnalysisResponse.OverallScore</c> is a fact about the (resume, posting) PAIR. Both are
/// 0..100 and neither explains the other; a client may show them side by side, but a combined figure
/// would be explainable as neither.
/// </para>
/// <para>
/// <b>A section whose <c>Breakdown.Weights.&lt;section&gt;</c> is 0 could not be measured</b> — in
/// <c>Breakdown.Weights</c> and beside its score in <c>Breakdown.Sections</c>. It neither helped nor hurt
/// this run, and the score printed next to it measures nothing. There is deliberately NO separate
/// "applicable" flag: the weight IS the signal, so the two can never disagree about the same fact. The
/// remaining weights are RENORMALIZED to still total 1.0, so the ceiling is 100 for every resume.
/// </para>
/// <para>
/// <b><c>weights.atsParseability</c> is 0 on every run this build can produce.</b> That section grades
/// what the UPLOADED DOCUMENT looked like to a parser, and the evidence for it does not exist yet: the
/// signed import-signals token is a separate change. Until it lands, the section is renormalized out and
/// the other four carry the whole score. When it does land, the signals will describe the last document
/// uploaded rather than the CV as it stands — in a product that deliberately never keeps the file, that
/// is the only thing ATS-parseability can honestly mean.
/// </para>
/// <para>
/// <b><c>impact</c> is measured, not estimated.</b> It is the exact increase in
/// <c>breakdown.weightedTotal</c> that acting on that one recommendation produces — computed by
/// evaluating the same rule with that one gap closed — on the same 0..1 scale as every score in this
/// response, NOT the 0..100 scale <see cref="ReadabilityScore"/> uses. A client showing it as points
/// multiplies by 100. <c>priority</c> is a pure function of it, so the label and the number can never
/// disagree.
/// </para>
/// <para>
/// <b>Impacts do not sum to the gap, and advice can be absent for a section scoring zero.</b> A resume
/// with no experience entries gets no Achievements advice at all, because "add a bullet point" names an
/// edit to a role that does not exist; it appears once the work history does. Advice a candidate cannot
/// act on is not emitted.
/// </para>
/// <para>
/// <b><c>weights.schemaVersion</c> names the readability MODEL</b> — the weights and the formulas
/// together — and it is its own number, unrelated to the scoring model's version. Two runs reporting
/// different versions were produced by different rules and are not comparable; two reporting the same
/// version can still carry different weights, because each run is renormalized to what could be measured
/// for that resume.
/// </para>
/// </remarks>
public sealed record ReadabilityResponse(
    Guid Id,
    ReadabilityBreakdownResponse Breakdown,
    Guid ResumeId,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<ReadabilityRecommendationResponse> Recommendations,
    int ReadabilityScore,
    string Band)
{
    public static ReadabilityResponse From(ReadabilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new ReadabilityResponse(
            report.Id.Value,
            ReadabilityBreakdownResponse.From(report.Breakdown),
            report.ResumeId.Value,
            report.EvaluatedAt,
            // Sorted HERE, and not merely trusted. ReadabilityReport.Recommendations is a SET: the child
            // table carries a surrogate key and no stored position, so a reloaded report hands them back
            // in whatever order the server chose. The Application layer sorts before persisting, to
            // decide which ten survive the cap; this sorts again, to decide what the candidate reads
            // first.
            [.. ReadabilityRecommendationOrder.Sort(report.Recommendations)
                .Select(ReadabilityRecommendationResponse.From)],
            report.ReadabilityScore,
            report.Band.ToString());
    }
}

public sealed record ReadabilityBreakdownResponse(
    double CompletenessScore,
    double ContactScore,
    double AchievementsScore,
    double ChronologyScore,
    double AtsParseabilityScore,
    ReadabilityWeightsResponse Weights,
    double WeightedTotal,
    IReadOnlyList<ReadabilitySectionScoreResponse> Sections)
{
    public static ReadabilityBreakdownResponse From(ReadabilityBreakdown breakdown) =>
        new(
            breakdown.CompletenessScore,
            breakdown.ContactScore,
            breakdown.AchievementsScore,
            breakdown.ChronologyScore,
            breakdown.AtsParseabilityScore,
            ReadabilityWeightsResponse.From(breakdown.Weights),
            breakdown.WeightedTotal,
            [.. breakdown.Sections.Select(ReadabilitySectionScoreResponse.From)]);
}

// Which weighting explained this run. SchemaVersion travels with it deliberately: a client comparing two
// runs from either side of a rule change needs to know the two are not measured in the same units, and
// the five weights beside it cannot tell them — a formula change moves a score without moving a weight.
public sealed record ReadabilityWeightsResponse(
    double Completeness,
    double Contact,
    double Achievements,
    double Chronology,
    double AtsParseability,
    int SchemaVersion)
{
    public static ReadabilityWeightsResponse From(ReadabilityWeightsSnapshot weights) =>
        new(
            weights.Completeness,
            weights.Contact,
            weights.Achievements,
            weights.Chronology,
            weights.AtsParseability,
            weights.SchemaVersion);
}

/// <summary>
/// One readability section's score paired with the weight it carried in this run. The pairing is the
/// whole reason the type exists: neither number explains the total on its own.
/// </summary>
/// <param name="Section">The section name — <c>"Chronology"</c>, never <c>3</c>.</param>
/// <param name="Score">
/// How this section read, 0..1. <b>Meaningless when <paramref name="Weight"/> is 0</b>: nothing could be
/// measured, so a client should not render it as a result.
/// </param>
/// <param name="Weight">
/// The share of the readability score this section carried, 0..1, after renormalization. <b>0 means the
/// section could not be measured for this resume</b>, so it neither helped nor hurt — this is the only
/// signal a client needs to explain "why is this section not counted", and there is deliberately no
/// parallel flag saying the same thing a second time. The weights across all five sections total 1.0,
/// the zero-weighted ones having been redistributed proportionally over the rest.
/// </param>
public sealed record ReadabilitySectionScoreResponse(string Section, double Score, double Weight)
{
    public static ReadabilitySectionScoreResponse From(ReadabilitySectionScore section) =>
        new(section.Section.ToString(), section.Score, section.Weight);
}

// Impact is on the same 0..1 scale as every score in this response, NOT the 0..100 scale ReadabilityScore
// uses. Priority reuses the scoring vocabulary ("Critical", "Important", "NiceToHave") deliberately: both
// engines derive it from the same 0..1 Impact scale through the same two thresholds, and a candidate
// reads one to-do list.
public sealed record ReadabilityRecommendationResponse(
    string Section,
    string Priority,
    string Kind,
    string Message,
    double Impact)
{
    public static ReadabilityRecommendationResponse From(ReadabilityRecommendation recommendation) =>
        new(
            recommendation.Section.ToString(),
            recommendation.Priority.ToString(),
            recommendation.Kind.ToString(),
            recommendation.Message,
            recommendation.Impact);
}

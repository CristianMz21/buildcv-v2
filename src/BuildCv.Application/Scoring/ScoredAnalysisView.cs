namespace BuildCv.Application.Scoring;

/// <summary>
/// What <c>POST /v1/scoring/score</c> answers with: an analysis, and the attribution computed against the
/// very resume that produced it.
/// </summary>
/// <remarks>
/// A SEPARATE TYPE, not two more fields on <see cref="AnalysisView"/>, and the separation is the safety
/// property. <see cref="AnalysisView"/> is shared by <c>GetAnalysisById</c> and <c>GetAnalysisHistory</c>,
/// which serve STORED analyses — rows whose resume may have been edited since, which is the whole reason
/// <c>IsStale</c> exists. Attribution on that type would compile, serialize, and quietly describe today's
/// resume beside a score taken from an older one.
///
/// Putting it here instead means the two read endpoints cannot return it by accident: they do not have it.
/// A convention would have needed a reviewer to notice; this needs a compiler.
/// </remarks>
public sealed record ScoredAnalysisView(
    AnalysisView View,
    IReadOnlyList<RequirementAttribution> RequirementMatches);

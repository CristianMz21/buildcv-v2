using System.Diagnostics;

namespace BuildCv.Application.Common.Observability;

/// <summary>
/// The one <see cref="ActivitySource"/> this product's own spans come from, plus the names and tag
/// keys they use.
/// </summary>
/// <remarks>
/// <para>
/// STATIC, unlike <see cref="BuildCvMetrics"/>, and the asymmetry is intentional. A trace is
/// correlated by parentage rather than by which host emitted it — <c>Activity.Current</c> is an
/// AsyncLocal, so a span started here is already attributed to the request that started it, and a test
/// distinguishes its own spans by trace id rather than by scope. A meter has no equivalent: a
/// measurement carries no parent, so the only thing that can attribute one to a host is the meter's
/// scope.
/// </para>
/// <para>
/// SPANS ARE FREE WHEN NOBODY IS LISTENING. <c>StartActivity</c> returns null unless an
/// <c>ActivityListener</c> has subscribed to this source, so with no exporter configured — which is
/// every deployment this build can produce — each call site costs one null check. That is what makes
/// instrumenting now and exporting later a real option rather than a cost paid up front.
/// </para>
/// <para>
/// TAG VALUES ARE FROM CLOSED SETS, for the same reason <see cref="BuildCvMetrics"/> says: an
/// exporter's backend is not covered by this repository's encryption, and a span attribute is as
/// exportable as a metric tag. The one client-controlled input that reaches a tag here — the declared
/// content type of an upload — is MAPPED to a closed format name before it goes on, never passed
/// through.
/// </para>
/// </remarks>
public static class BuildCvActivities
{
    /// <summary>The source name an exporter subscribes to.</summary>
    public const string SourceName = "BuildCv";

    /// <summary>The synchronous parse of an uploaded document — the heaviest work in the API.</summary>
    public const string DocumentExtract = "buildcv.document.extract";

    /// <summary>One scoring request, including the de-duplicated shape that never reaches the engine.</summary>
    public const string ResumeScore = "buildcv.resume.score";

    /// <summary>One readability evaluation.</summary>
    public const string ResumeReadability = "buildcv.resume.readability";

    /// <summary>Closed format name of an uploaded document. Never the declared content type itself.</summary>
    public const string DocumentFormatTag = "buildcv.document.format";

    /// <summary>Whether the parse produced text or a refusal. Never the refusal MESSAGE.</summary>
    public const string DocumentOutcomeTag = "buildcv.document.outcome";

    /// <summary>One of <see cref="ScoringOutcomes"/>.</summary>
    public const string ScoringOutcomeTag = "buildcv.scoring.outcome";

    public const string Extracted = "extracted";
    public const string Failed = "failed";

    public static ActivitySource Source { get; } = new(SourceName);
}

using System.Diagnostics.Metrics;

namespace BuildCv.Application.Common.Observability;

/// <summary>
/// The one <see cref="Meter"/> this product's own counters hang off, and the only way to increment
/// them.
/// </summary>
/// <remarks>
/// <para>
/// A TYPE RATHER THAN FOUR SCATTERED <c>Counter</c> FIELDS, because the thing that has to be
/// enforceable is not the arithmetic — it is the TAGS. A metric tag is a dimension of a time series:
/// every distinct value creates a new series in the backend, and unlike this repository's columns,
/// tags are covered by NONE of its encryption. So a tag carrying an account id, a resume id, a skill
/// name or anything else derived from what a candidate typed does two things at once — it turns a
/// metrics backend into an unencrypted PII store, and it multiplies the series count by the number of
/// users. Every method here therefore takes a value from a CLOSED set named in code
/// (<see cref="ScoringOutcomes"/>, <see cref="ThrottlePolicies"/>,
/// <c>DocumentExtractionFailureReasons</c>) and there is no overload that accepts caller-supplied
/// dimensions. Two tests execute that rather than trusting it:
/// <c>ObservabilityMetricsTests.EveryTagValueTheseRoutesEmit_ComesFromAClosedSet</c> drives the API
/// paths, and <c>DocumentTextExtractionTests.TheReasonsThisSuiteEmits_AreExactlyTheDeclaredSetLessThePasswordCase</c>
/// checks the extraction reasons in BOTH directions — every emitted value is declared, and every
/// declared value is reachable — so a set cannot be widened to make an assertion pass.
/// </para>
/// <para>
/// INSTANCE, NOT STATIC — unlike <c>AesGcmFieldEncryptor</c>'s meter, and the difference is
/// deliberate. The <c>scope</c> argument on the <see cref="Meter"/> constructor is the mechanism
/// <c>IMeterFactory</c> itself uses: it stamps the meter with the object that owns it, so a
/// <c>MeterListener</c> can tell one composed host's measurements from another's. This class passes
/// ITSELF, which makes the stamp exactly as unique as the singleton — one per host — and lets a test
/// resolve <c>BuildCvMetrics</c> and filter on reference equality with no assumption about which
/// service-provider object a factory callback happens to receive. Two hosts in one process, which is
/// what an xUnit assembly full of <c>WebApplicationFactory</c> instances is, therefore publish to two
/// distinguishable meters instead of one shared global. Without that, a metric assertion could be
/// satisfied by a measurement some other test produced — a green that means nothing.
/// </para>
/// <para>
/// <c>IMeterFactory</c> is not used because it would need <c>AddMetrics()</c>, which lives in
/// <c>Microsoft.Extensions.Diagnostics</c> — part of the ASP.NET Core shared framework and therefore
/// not reachable from this project or from Infrastructure, both plain library SDK projects. The scope
/// argument gives the property that matters with nothing added.
/// </para>
/// </remarks>
public sealed class BuildCvMetrics : IDisposable
{
    /// <summary>
    /// The meter name an exporter subscribes to. Note it does NOT cover
    /// <c>BuildCv.Infrastructure.Encryption</c>, the pre-existing meter behind
    /// <c>buildcv.encryption.operations</c> — meter names are matched exactly, so an exporter must
    /// name both.
    /// </summary>
    public const string MeterName = "BuildCv";

    public const string ScoringRunsInstrument = "buildcv.scoring.runs";
    public const string ReadabilityReportsInstrument = "buildcv.readability.reports";
    public const string ExtractionFailuresInstrument = "buildcv.documents.extraction_failures";
    public const string ThrottleRejectionsInstrument = "buildcv.throttle.rejections";

    public const string OutcomeTag = "outcome";
    public const string ReasonTag = "reason";
    public const string PolicyTag = "policy";

    private readonly Meter _meter;
    private readonly Counter<long> _scoringRuns;
    private readonly Counter<long> _readabilityReports;
    private readonly Counter<long> _extractionFailures;
    private readonly Counter<long> _throttleRejections;

    public BuildCvMetrics()
    {
        // scope: this — see the remarks. A listener compares Instrument.Meter.Scope against the
        // BuildCvMetrics it resolved, which is the narrowest identity available and needs nothing
        // exposed to make it comparable.
        _meter = new Meter(MeterName, version: null, tags: null, scope: this);

        // ONE counter with an outcome tag rather than two counters, because the number anyone will
        // actually ask for is the RATIO — how often de-duplication saved a scoring run — and a ratio
        // across two independent counters is a query the backend has to be told how to write. Tagged,
        // it is one series family and the total is still the sum.
        _scoringRuns = _meter.CreateCounter<long>(
            ScoringRunsInstrument,
            unit: "{run}",
            description: "Scoring requests that reached the engine, tagged by whether a stored analysis was reused.");

        _readabilityReports = _meter.CreateCounter<long>(
            ReadabilityReportsInstrument,
            unit: "{report}",
            description: "Readability reports produced. Untagged: this path has no de-duplication and no variants.");

        _extractionFailures = _meter.CreateCounter<long>(
            ExtractionFailuresInstrument,
            unit: "{failure}",
            description: "Uploaded documents this API refused to read, tagged by which refusal it was.");

        _throttleRejections = _meter.CreateCounter<long>(
            ThrottleRejectionsInstrument,
            unit: "{rejection}",
            description: "Requests refused by a rate limiter, tagged by which limiter refused them.");
    }

    /// <param name="outcome">One of <see cref="ScoringOutcomes"/>.</param>
    public void ScoringRun(string outcome) =>
        _scoringRuns.Add(1, new KeyValuePair<string, object?>(OutcomeTag, outcome));

    public void ReadabilityReportProduced() => _readabilityReports.Add(1);

    /// <param name="reason">
    /// One of <c>BuildCv.Infrastructure.Documents.DocumentExtractionFailureReasons</c>. It is a short
    /// classification, never the failure MESSAGE and never anything the parser read out of the file —
    /// the messages are prose written for a candidate, and the file is someone's complete CV.
    /// </param>
    public void ExtractionFailed(string reason) =>
        _extractionFailures.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason));

    /// <param name="policy">One of <see cref="ThrottlePolicies"/>.</param>
    public void ThrottleRejection(string policy) =>
        _throttleRejections.Add(1, new KeyValuePair<string, object?>(PolicyTag, policy));

    public void Dispose() => _meter.Dispose();
}

namespace BuildCv.Application.Common.Observability;

/// <summary>
/// Every value the <c>outcome</c> tag on <c>buildcv.scoring.runs</c> may take. Two, and it stays two:
/// this is a dimension of a time series, so the set's size is the series count.
/// </summary>
public static class ScoringOutcomes
{
    /// <summary>The engine ran and a new analysis was appended.</summary>
    public const string Computed = "computed";

    /// <summary>
    /// A stored analysis was reused because nothing that feeds the score had moved. M1's
    /// de-duplication is invisible without this: it costs a write, saves an engine run, and neither
    /// shows up in a request count.
    /// </summary>
    public const string Deduplicated = "deduplicated";

    public static IReadOnlyList<string> All { get; } = [Computed, Deduplicated];
}

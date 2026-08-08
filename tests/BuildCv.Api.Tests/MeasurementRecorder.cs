using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BuildCv.Application.Common.Observability;

namespace BuildCv.Api.Tests;

// Captures every measurement one host's BuildCvMetrics publishes, and nothing else.
//
// THE SCOPE FILTER IS THE POINT. A MeterListener is process-global: without it this would also see
// measurements from every other WebApplicationFactory an xUnit run has alive at the same moment, and
// an assertion satisfied by another test's request is a green that proves nothing. BuildCvMetrics
// stamps its meter with itself, so comparing Meter.Scope against the instance resolved from THIS
// factory is exact.
//
// Long is the only measurement type read, because every instrument on that meter is a Counter<long>.
// A future Histogram<double> would need a second callback, and would go unrecorded until it had one —
// which is why the tests assert on named instruments rather than on "everything that arrived".
internal sealed class MeasurementRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<RecordedMeasurement> _measurements = new();

    public MeasurementRecorder(BuildCvMetrics metrics)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, metrics))
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Enqueue(new RecordedMeasurement(instrument.Name, value, [.. tags])));

        // Start replays InstrumentPublished for instruments that already exist and keeps receiving the
        // ones created later, so it does not matter whether the host has built its metrics singleton
        // yet at the moment a test constructs this.
        _listener.Start();
    }

    public IReadOnlyList<RecordedMeasurement> Measurements => [.. _measurements];

    public IReadOnlyList<string> TagValuesOf(string instrument, string tag) =>
    [
        .. _measurements
            .Where(measurement => measurement.Instrument == instrument)
            .SelectMany(measurement => measurement.Tags)
            .Where(pair => pair.Key == tag)
            .Select(pair => pair.Value?.ToString() ?? string.Empty)
    ];

    // Every string any measurement carries — instrument names, tag keys and tag values. What an
    // absence assertion has to search.
    public IReadOnlyList<string> AllText =>
    [
        .. _measurements.SelectMany(measurement =>
            new[] { measurement.Instrument }
                .Concat(measurement.Tags.Select(pair => pair.Key))
                .Concat(measurement.Tags.Select(pair => pair.Value?.ToString() ?? string.Empty)))
    ];

    public void Dispose() => _listener.Dispose();
}

internal sealed record RecordedMeasurement(
    string Instrument, long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);

using System.Collections.Concurrent;
using System.Diagnostics;

namespace BuildCv.Api.Tests;

// Captures every Activity produced while it is alive — from EVERY source, not just this repository's.
//
// The wider net is deliberate and is what makes the leak assertion worth making: a span this code
// never wrote is exactly where an unnoticed leak would sit, because ASP.NET Core, HttpClient and EF
// Core all tag their own activities from request state. Listening only to "BuildCv" would certify the
// spans someone already thought about.
//
// AllData, not PropagationData: anything less and tags are never recorded, so every absence assertion
// downstream would be vacuously true.
internal sealed class ActivityRecorder : IDisposable
{
    private readonly ActivityListener _listener = new();
    private readonly ConcurrentQueue<Activity> _activities = new();

    public ActivityRecorder()
    {
        _listener.ShouldListenTo = _ => true;
        _listener.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData;
        _listener.SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData;
        _listener.ActivityStopped = _activities.Enqueue;
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Activities => [.. _activities];

    // Every string an activity carries: its names, its tags (keys and values), its events and their
    // tags, and its baggage. Baggage is included because it PROPAGATES to downstream services, which
    // makes it the worst of these to leak into.
    public IReadOnlyList<string> AllText
    {
        get
        {
            var text = new List<string>();
            foreach (var activity in _activities)
            {
                text.Add(activity.OperationName);
                text.Add(activity.DisplayName);

                foreach (var tag in activity.TagObjects)
                {
                    text.Add(tag.Key);
                    text.Add(tag.Value?.ToString() ?? string.Empty);
                }

                foreach (var item in activity.Baggage)
                {
                    text.Add(item.Key);
                    text.Add(item.Value ?? string.Empty);
                }

                foreach (var activityEvent in activity.Events)
                {
                    text.Add(activityEvent.Name);
                    foreach (var tag in activityEvent.Tags)
                    {
                        text.Add(tag.Key);
                        text.Add(tag.Value?.ToString() ?? string.Empty);
                    }
                }
            }

            return text;
        }
    }

    public void Dispose() => _listener.Dispose();
}

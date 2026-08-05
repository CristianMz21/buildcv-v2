namespace BuildCv.Application.Tests.Fakes;

// A clock that MOVES ON EVERY READ, which is the only way a second GetUtcNow() becomes observable.
//
// FakeTimeProvider is frozen until something calls Advance, so a handler reading it twice gets the same
// instant twice and a test built on it cannot tell one read from two. This one hands out `start`, then
// `start + step`, then `start + 2*step`, so seeding it just before midnight with a step that crosses it
// makes the second read land on a different DATE -- the difference the scoring row is stamped with.
public sealed class AdvancingTimeProvider(DateTimeOffset start, TimeSpan step) : TimeProvider
{
    private DateTimeOffset _now = start;

    // Counts the reads, so "exactly one clock snapshot" can be asserted directly rather than inferred
    // from the dates agreeing -- two reads inside the same second would agree by luck.
    public int ReadCount { get; private set; }

    public override DateTimeOffset GetUtcNow()
    {
        ReadCount++;
        var current = _now;
        _now = _now.Add(step);
        return current;
    }
}

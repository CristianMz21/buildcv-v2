namespace BuildCv.Application.Tests.Fakes;

using System.Text;
using BuildCv.Application.Common.Services;

// A lexicon a test states in one line, so a scoring test says which aliases it depends on instead of
// depending on whatever the shipped data happens to hold today.
//
// It reimplements the contract rather than wrapping the Infrastructure adapter, and that is the point:
// BuildCv.Application.Tests references Application and Domain only, so the Application scoring suites
// cannot silently start asserting the shipped table's behaviour. The shipped table is exercised where it
// lives, in BuildCv.Infrastructure.Tests.
//
// Empty is what makes the additive-only property testable: with no entries Canonicalize is the identity,
// so every existing scoring expectation must hold bit for bit against it.
public sealed class FakeSkillLexicon : ISkillLexicon
{
    // The whole Application scoring suite runs against this. It is a field rather than a property with a
    // fresh instance per read so a test cannot accidentally get a lexicon nobody else has.
    public static readonly FakeSkillLexicon Empty = new([]);

    private readonly Dictionary<string, string> _canonicalByKey;

    private FakeSkillLexicon(IReadOnlyList<(string Alias, string Canonical)> entries)
    {
        _canonicalByKey = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (alias, canonical) in entries)
        {
            // Canonical tokens are keys of their own, exactly as the real adapter does it: a lookup of the
            // canonical spelling must not fall through to the unchanged-term path, or the table's shape
            // would decide whether "React" reaches "React" and the fake would stop modelling the port.
            _canonicalByKey[Key(canonical)] = canonical;
            _canonicalByKey[Key(alias)] = canonical;
        }

        Version = $"fake:{_canonicalByKey.Count}:{string.Join('|', _canonicalByKey.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}"))}";
    }

    public string Version { get; }

    // Stated as (alias -> canonical) pairs because that is the direction a reader checks: "what does this
    // spelling become". The canonical side is repeated across a skill's aliases, which keeps every pair
    // readable on its own line at the call site.
    public static FakeSkillLexicon With(params (string Alias, string Canonical)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new FakeSkillLexicon(entries);
    }

    public string Canonicalize(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        // The SAME instance back for an unrecognised term, not an equal one. Rule 2 of the port contract:
        // it is what makes an empty lexicon a provable no-op rather than a probable one.
        return _canonicalByKey.TryGetValue(Key(term), out var canonical) ? canonical : term;
    }

    // Applied to the table's keys and to every lookup, so the two cannot disagree about what "the same
    // spelling" means.
    private static string Key(string term) =>
        string.Join(' ', term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Normalize(NormalizationForm.FormC)
            .ToLowerInvariant();
}

namespace BuildCv.Infrastructure.Lexicon;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BuildCv.Application.Common.Services;

/// <summary>
/// The shipped <see cref="ISkillLexicon"/>, read from the embedded <c>SkillLexicon.txt</c>. The rules
/// that decide what may go in that file are stated in the file, where whoever edits it will be looking.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Load"/> is called from <c>AddInfrastructure</c>, so malformed data is a startup failure
/// rather than a first-request one.</b> A lazily-initialized static would have reported the same fault as
/// a <c>TypeInitializationException</c> whose message says nothing, on whichever request happened to
/// score first — and the faults this parser raises (a duplicate key, an empty field) are exactly the ones
/// whose message is the whole diagnosis.
/// </para>
/// <para>
/// <b>The instance is immutable and registered as a singleton</b>, which is what lets the singleton
/// <c>ScoringEngine</c> hold it.
/// </para>
/// </remarks>
public sealed class SkillLexicon : ISkillLexicon
{
    // Stated rather than left to the default $(RootNamespace).<folder>.<file> derivation, and matched by
    // an explicit LogicalName in the csproj. The derived name depends on the folder the file sits in, so
    // moving the file would turn a compile-time concern into a MissingManifestResourceException.
    private const string ResourceName = "BuildCv.Infrastructure.Lexicon.SkillLexicon.txt";

    private const char CommentMarker = '#';
    private const char FieldSeparator = '|';

    // Ordinal, because every key that goes in has already been through Key(): the culture-sensitive
    // comparers would fold a SECOND time, on rules that vary by culture, over strings that are already
    // folded. StringComparer.OrdinalIgnoreCase would silently make the lower-casing in Key() dead code
    // and hide a key that had skipped it.
    private readonly Dictionary<string, string> _canonicalByKey;

    private SkillLexicon(Dictionary<string, string> canonicalByKey)
    {
        _canonicalByKey = canonicalByKey;
        Version = ComputeVersion(canonicalByKey);
        CanonicalTokens = [.. canonicalByKey.Values.Distinct(StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public string Version { get; }

    /// <summary>The canonical token of every skill in the file, in no particular order.</summary>
    /// <remarks>
    /// Exposed for the tests that assert properties of the DATA — that the canonical set is exactly the
    /// extractor's vocabulary, and that no two entries collide. A scoring path has no use for it:
    /// matching asks about two terms it already holds, never about what else exists.
    /// </remarks>
    public IReadOnlyList<string> CanonicalTokens { get; }

    /// <summary>Reads and parses the embedded lexicon. Throws if the data is malformed.</summary>
    public static SkillLexicon Load() => FromData(ReadEmbeddedData());

    /// <inheritdoc />
    public string Canonicalize(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        // THE VERY INSTANCE BACK for an unrecognised term — rule 2 of the port contract, and the whole
        // of what makes this additive. With no entry to find, this method is the identity, so the caller
        // comparing two canonical forms is running character-for-character the comparison it already ran.
        return _canonicalByKey.TryGetValue(Key(term), out var canonical) ? canonical : term;
    }

    /// <summary>Parses lexicon data in the embedded file's format. Internal so tests can feed it data.</summary>
    internal static SkillLexicon FromData(string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var canonicalByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        // Split on '\n' and Trim, rather than on the environment's newline: this file is compiled in from
        // a repository that can be checked out with either line ending.
        var lines = data.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line[0] == CommentMarker)
                continue;

            var fields = line.Split(FieldSeparator);
            var canonical = fields[0].Trim();
            var lineNumber = index + 1;

            if (canonical.Length == 0)
                throw new InvalidOperationException($"Skill lexicon line {lineNumber} has no canonical token.");

            // The canonical token is a key of its own, so a lookup of the canonical spelling is answered
            // by the table rather than by the unrecognised-term fall-through. Both answer "React" for
            // "React" today; only this one keeps doing so if a canonical token ever stops being a
            // spelling anybody types.
            Add(canonicalByKey, canonical, canonical, lineNumber);

            for (var field = 1; field < fields.Length; field++)
            {
                var alias = fields[field].Trim();
                if (alias.Length == 0)
                    throw new InvalidOperationException(
                        $"Skill lexicon line {lineNumber} has an empty alias field. A stray '{FieldSeparator}' "
                        + "would otherwise map the empty string onto a real skill.");

                Add(canonicalByKey, alias, canonical, lineNumber);
            }
        }

        return new SkillLexicon(canonicalByKey);
    }

    // A REPEATED KEY IS A LOAD FAILURE, not a last-one-wins overwrite. Two skills sharing a key is the
    // false match the file's rules exist to prevent, and an overwrite would introduce one silently and
    // make the outcome depend on line order. The message names both canonicals because the fault is the
    // PAIR: neither line is wrong on its own.
    private static void Add(Dictionary<string, string> table, string term, string canonical, int lineNumber)
    {
        var key = Key(term);
        if (table.TryGetValue(key, out var existing))
            throw new InvalidOperationException(
                $"Skill lexicon line {lineNumber}: '{term}' already maps to '{existing}' and cannot also "
                + $"map to '{canonical}'. Two skills sharing a spelling would match each other.");

        table.Add(key, canonical);
    }

    // Applied to the file's keys AND to every lookup, from one method, so the two cannot disagree about
    // what counts as the same spelling.
    //
    // NFC because Technology.Create applies it, so a Skill.Name and a JobRequirement's name arrive
    // composed — but Skill.Keywords is a raw string list with no normalization pass anywhere, so a
    // decomposed keyword can reach this and would miss a composed key.
    //
    // Whitespace is collapsed, not merely trimmed, so the file can write "ms sql server" once instead of
    // guessing how many spaces a candidate typed. ToLowerInvariant, never ToLower(): the Turkish 'I'
    // folds to a dotless 'ı' under tr-TR, which would make recognition depend on the server's culture.
    private static string Key(string term) =>
        string.Join(' ', term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Normalize(NormalizationForm.FormC)
            .ToLowerInvariant();

    // A digest of the PARSED TABLE, not of the file's bytes. It answers "which lexicon produced this
    // answer", so reformatting the file, reordering its lines or rewriting its comments must not change
    // it — and adding, removing or repointing a single alias must. Derived rather than hand-maintained
    // because a hand-maintained number is exactly the thing an author forgets in the commit that mattered.
    //
    // Eight bytes, hex: enough that two curated tables will not collide, short enough to read in a test
    // failure and to paste into a commit message.
    private static string ComputeVersion(Dictionary<string, string> table)
    {
        var canonicalForm = new StringBuilder();
        foreach (var entry in table.OrderBy(e => e.Key, StringComparer.Ordinal))
            canonicalForm.Append(entry.Key).Append('\t').Append(entry.Value).Append('\n');

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalForm.ToString()));
        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }

    private static string ReadEmbeddedData()
    {
        using var stream = typeof(SkillLexicon).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded skill lexicon '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

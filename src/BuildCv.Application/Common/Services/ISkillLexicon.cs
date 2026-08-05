namespace BuildCv.Application.Common.Services;

/// <summary>
/// Decides whether two spellings name the same skill, by folding each one to a canonical token.
/// <c>"React.js"</c> and <c>"React"</c> are the same skill; <c>"Java"</c> and <c>"JavaScript"</c> are not.
/// </summary>
/// <remarks>
/// <para>
/// <b>IT IS A SCORING INPUT.</b> <c>ScoringRules.IsSatisfiedBy</c> consults it, so replacing the data
/// behind an implementation changes what a given <c>(resume, posting)</c> pair scores exactly as moving a
/// weight would. That is why <see cref="Version"/> is on the port and why
/// <c>ScoringWeightsSnapshot.CurrentSchemaVersion</c> has to be bumped alongside a revision — the rule is
/// stated on that constant.
/// </para>
/// <para>
/// <b>CANONICALIZATION MERGES, SO A BAD ENTRY IS WORSE THAN A MISSING ONE.</b> An entry that folds two
/// genuinely different skills onto one token tells a candidate they meet a requirement they do not, in
/// the section they most needed advice on. A missing entry only costs them advice they can still act on.
/// Implementations must resolve that asymmetry toward missing.
/// </para>
/// <para>
/// <b>THE CONTRACT, in three rules an implementation must satisfy:</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>Deterministic and pure.</b> The same argument yields the same answer for the lifetime of the
/// process, and calling it has no observable effect. The engine that consults it is registered as a
/// singleton and shared across every request.
/// </item>
/// <item>
/// <b>An unrecognised term is returned unchanged</b> — the same instance that was passed in, not merely
/// an equal string. This is what makes the matching rule ADDITIVE: with an empty lexicon,
/// <c>Canonicalize</c> is the identity, so comparing two canonical forms is character-for-character the
/// same test as comparing the two originals, and no candidate's score can move. Anything that trimmed,
/// case-folded or stripped punctuation on the way out would smuggle a matching rule past the data.
/// </item>
/// <item>
/// <b>Recognition is case-, whitespace- and Unicode-insensitive.</b> <c>Technology.Create</c> applies
/// Trim + NFC, but <c>Skill.Keywords</c> is a raw string list with no such pass, so an implementation
/// that compared its table's keys byte-for-byte would fail to recognise a term the Domain considers
/// identical.
/// </item>
/// </list>
/// <para>
/// <b>The signature is the enforcement of rule 1 that a comment cannot be.</b> There is no
/// <c>CancellationToken</c> and no <c>Task</c>, so there is nowhere to await: an I/O-backed adapter would
/// have to block a request thread to exist at all. It does not make one impossible — nothing in C# does
/// — it makes one visibly wrong at the call site.
/// </para>
/// </remarks>
public interface ISkillLexicon
{
    /// <summary>
    /// Which lexicon DATA produced an answer. Two implementations agreeing on this string are expected to
    /// agree on every <see cref="Canonicalize"/> call; a revision must change it.
    /// </summary>
    /// <remarks>
    /// Nothing in the scoring path reads this. Its consumer is the test that pins the shipped data
    /// against <c>ScoringWeightsSnapshot.CurrentSchemaVersion</c>, so that changing the one without the
    /// other fails a build rather than silently stamping two different scoring models with one version.
    /// </remarks>
    string Version { get; }

    /// <summary>
    /// The canonical token for <paramref name="term"/>, or <paramref name="term"/> itself when the
    /// lexicon does not recognise it.
    /// </summary>
    /// <param name="term">A skill name as a candidate or a recruiter typed it. Never <c>null</c>.</param>
    /// <returns>
    /// A token to compare against another canonical token with <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// It is NOT a display value — two spellings a human would keep apart can share one.
    /// </returns>
    string Canonicalize(string term);
}

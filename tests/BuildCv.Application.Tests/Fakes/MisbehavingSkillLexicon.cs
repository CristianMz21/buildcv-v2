namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Services;

// A lexicon that BREAKS the port contract on purpose, so the exact-match-first ordering in
// ScoringRules.NamesTheSameSkill is observable at all.
//
// WHY THIS TYPE HAS TO EXIST. Removing that first operand and leaving only the canonical comparison reds
// NOTHING against any conforming implementation — measured, not assumed. It cannot: ISkillLexicon rule 2
// says an unrecognised term comes back unchanged and rule 3 says recognition ignores case, and together
// those make "the canonical forms are OrdinalIgnoreCase-equal" true wherever "the originals are" is. The
// ordering is therefore not what makes a CONFORMING lexicon additive; the contract is.
//
// What the ordering buys is that additivity does not DEPEND on the contract being honoured. Every
// implementation, conforming or not, keeps every match the previous engine made — no candidate's score
// falls because an adapter was written badly, only because the file says something wrong. That is a real
// guarantee about the code and it needs an implementation that would otherwise break it to be a testable
// one, which is this.
//
// It answers case-sensitively, which violates rule 3: "React" is recognised, "react" is not.
public sealed class MisbehavingSkillLexicon : ISkillLexicon
{
    private readonly Dictionary<string, string> _canonicalByExactSpelling;

    private MisbehavingSkillLexicon(Dictionary<string, string> canonicalByExactSpelling) =>
        _canonicalByExactSpelling = canonicalByExactSpelling;

    public string Version => "misbehaving";

    public static MisbehavingSkillLexicon RecognisingOnly(string spelling, string canonical) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { [spelling] = canonical });

    public string Canonicalize(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return _canonicalByExactSpelling.TryGetValue(term, out var canonical) ? canonical : term;
    }
}

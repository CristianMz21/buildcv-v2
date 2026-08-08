namespace BuildCv.Application.Readability;

using System.Globalization;
using System.Text;

// The closed vocabulary the Achievements rule reads: does this bullet point BEGIN with a word that says
// what the candidate DID?
//
// A curated set in code rather than a data file, unlike the skill lexicon. The difference is what the
// data is for: the lexicon merges two spellings of one skill, so a careless entry silently reports a
// requirement satisfied that is not, and it is reviewed as data with a collision suite. This set decides
// only whether one bullet point out of a candidate's own resume reads as an achievement, its worst
// failure is one sixth of one section of an advisory score, and keeping it beside the rule that reads it
// means "which words count" is answerable from the file that asks the question.
//
// BOTH LANGUAGES, because the product's users are Spanish-speaking job seekers writing CVs in Spanish,
// in English, or in both at once. A list of English verbs alone would tell every candidate who wrote
// "Lideré el equipo de pagos" that their resume states no achievements -- authoritative, and wrong in
// the direction that costs the candidate, which is the same failure shape Language.Fluency was sealed to
// prevent.
//
// CASE- AND ACCENT-INSENSITIVE. Every entry below is folded through the same Fold() the lookup uses, so
// the table and the lookup cannot disagree about what "the same word" means, and "Lideré", "lidere" and
// a decomposed "lideré" all reach the same entry. Folding removes combining marks, so 'ñ' folds to
// 'n', so "diseñé" and a candidate who typed "disene" reach one entry.
//
// THAT MERGE IS SAFE HERE IN A WAY IT WOULD NOT BE IN THE SKILL LEXICON, and the reason is the data
// structure rather than the vocabulary. This is a SET MEMBERSHIP test, so two entries that folded
// together would be absorbed by HashSet.Add and change nothing; the lexicon is a MAPPING, where a
// collision silently makes one skill answer for another. Nothing here can be merged INTO anything -- a
// folded word either is one of these verbs or is not.
//
// A REVISION OF THIS SET IS A MODEL CHANGE. It is data a scoring rule consults, so adding or removing a
// verb moves what a given resume scores and bumps ReadabilityWeightsSnapshot.CurrentSchemaVersion --
// the same bump rule a lexicon revision follows on the matching side.
internal static class ActionVerbs
{
    // Whitespace and the glyphs a bullet point is commonly typed with. Skipped before the first word is
    // read, so "- Led the migration" and "• Lideré la migración" are verb-led exactly as the same
    // sentences without the glyph are. Deliberately NOT skipping digits: "3 servers were migrated" does
    // not begin with a verb, and pretending it does would pay a candidate for the wrong edit.
    private const string BulletGlyphs = "-–—•·‣▪*>»";

    private static readonly HashSet<string> Verbs = BuildVerbSet(
    [
        // English.
        "achieved", "analysed", "analyzed", "architected", "authored", "automated", "built", "closed",
        "coordinated", "created", "cut", "defined", "delivered", "deployed", "designed", "developed",
        "documented", "drove", "established", "expanded", "grew", "implemented", "improved",
        "increased", "integrated", "introduced", "launched", "led", "maintained", "managed", "mentored",
        "migrated", "modernised", "modernized", "monitored", "negotiated", "optimised", "optimized",
        "owned", "planned", "rebuilt", "redesigned", "reduced", "refactored", "resolved", "saved",
        "scaled", "secured", "shipped", "simplified", "standardised", "standardized", "streamlined",
        "supported", "tested", "trained", "wrote",

        // Spanish. First-person preterite is how a CV bullet point is written in Spanish; the
        // infinitives beside them cover the impersonal style a candidate may use instead.
        "administré", "administrar", "ahorré", "aumenté", "automaticé", "construí", "construir",
        "coordiné", "creé", "definí", "desarrollé", "desarrollar", "desplegué", "diseñé", "diseñar",
        "dirigí", "dirigir", "documenté", "entregué", "escribí", "establecí", "gestioné", "gestionar",
        "implementé", "implementar", "impulsé", "incrementé", "integré", "introduje", "lancé", "lideré",
        "liderar", "logré", "mantuve", "mejoré", "mejorar", "migré", "migrar", "modernicé", "monitoreé",
        "negocié", "optimicé", "optimizar", "planifiqué", "probé", "redefiní", "rediseñé", "reduje",
        "refactoricé", "resolví", "simplifiqué", "supervisé",
    ]);

    // Does this bullet point begin with a word from the set?
    internal static bool StartsWithAnActionVerb(string highlight)
    {
        ArgumentNullException.ThrowIfNull(highlight);

        var word = LeadingWord(highlight);
        return word.Length > 0 && Verbs.Contains(word);
    }

    // The first run of LETTERS, after any leading whitespace and bullet glyphs. Stopping at the first
    // non-letter is what makes "Reduced: latency by 40%" and "Reduced latency by 40%" the same word,
    // without a punctuation list to keep in step with anything.
    private static string LeadingWord(string text)
    {
        var index = 0;
        while (index < text.Length
            && (char.IsWhiteSpace(text[index]) || BulletGlyphs.Contains(text[index], StringComparison.Ordinal)))
        {
            index++;
        }

        var start = index;
        while (index < text.Length && char.IsLetter(text[index]))
            index++;

        return Fold(text[start..index]);
    }

    private static HashSet<string> BuildVerbSet(IEnumerable<string> verbs)
    {
        // Ordinal, over folded keys. A case-insensitive comparer here would be a SECOND opinion about
        // case beside Fold(), and the two would be free to disagree about a culture-specific casing.
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var verb in verbs)
            set.Add(Fold(verb));
        return set;
    }

    // Lowercase, invariant, with combining marks removed. Applied to the table's keys and to every
    // lookup, so the two cannot disagree.
    private static string Fold(string word)
    {
        var lowered = word.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(lowered.Length);

        foreach (var character in lowered)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

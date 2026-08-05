namespace BuildCv.Application.Resumes;

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;

/// <summary>
/// Maps the bilingual words a CV uses for proficiency and seniority onto the domain's closed enums — but
/// ONLY when the word is actually there. A missing level is left null and flagged, never defaulted: the
/// enum values exist for the candidate to pick, and inventing "Intermediate" for a skill that names no
/// level is exactly the silent guess this PR forbids.
/// </summary>
public static class LevelWords
{
    /// <summary>
    /// The skill level named in PARENTHESES, e.g. <c>"Python (Advanced)"</c> or <c>"SQL (Avanzado)"</c>,
    /// with <paramref name="withoutLevel"/> set to the name minus that parenthetical. Only parentheses
    /// count — a dash could be part of the skill's own name, and reading it as a level would corrupt it.
    /// </summary>
    public static SkillLevel? SkillLevelIn(string token, out string? withoutLevel)
    {
        withoutLevel = null;

        var open = token.IndexOf('(');
        var close = token.IndexOf(')');
        if (open <= 0 || close <= open)
            return null;

        var inside = ResumeSectionHeadings.Normalize(token[(open + 1)..close]);
        SkillLevel? level = inside switch
        {
            _ when Has(inside, "expert", "experto", "experta") => SkillLevel.Expert,
            _ when Has(inside, "advanced", "avanzado", "avanzada") => SkillLevel.Advanced,
            _ when Has(inside, "intermediate", "intermedio", "intermedia") => SkillLevel.Intermediate,
            _ when Has(inside, "beginner", "basic", "basico", "basica", "principiante") => SkillLevel.Beginner,
            _ => null
        };

        if (level is not null)
            withoutLevel = token[..open].Trim();
        return level;
    }

    /// <summary>The language proficiency named in a fluency phrase, or null when none maps.</summary>
    public static LanguageProficiency? LanguageLevelIn(string fluency)
    {
        var text = ResumeSectionHeadings.Normalize(fluency);
        return text switch
        {
            _ when Has(text, "native", "nativo", "nativa", "lengua materna", "mother tongue", "c2") =>
                LanguageProficiency.Native,
            _ when Has(text, "fluent", "fluido", "fluida", "bilingual", "bilingue", "c1") =>
                LanguageProficiency.Fluent,
            _ when Has(text, "professional", "profesional", "b2") =>
                LanguageProficiency.Professional,
            _ when Has(text, "conversational", "conversacional", "intermediate", "intermedio", "b1") =>
                LanguageProficiency.Conversational,
            _ when Has(text, "basic", "basico", "principiante", "elementary", "elemental", "a1", "a2") =>
                LanguageProficiency.Basic,
            _ => null
        };
    }

    /// <summary>The education level a degree phrase names, or null when none maps.</summary>
    /// <remarks>
    /// Ordered most-advanced first so "posgrado" and "maestría" are read as Master before the substring
    /// "grado" inside them could be read as Bachelor.
    /// </remarks>
    public static EducationLevel? EducationLevelIn(string degree)
    {
        var text = ResumeSectionHeadings.Normalize(degree);
        return text switch
        {
            _ when Has(text, "doctorate", "doctorado", "phd", "ph d", "doctor") => EducationLevel.Doctorate,
            _ when Has(text, "master", "maestria", "mba", "posgrado", "postgrado") => EducationLevel.Master,
            _ when Has(text, "bachelor", "grado", "licenciatura", "licenciado", "licenciada",
                "ingenieria", "ingeniero", "ingeniera") => EducationLevel.Bachelor,
            _ when Has(text, "associate", "tecnico", "tecnica", "tecnicatura", "tecnologo") =>
                EducationLevel.Associate,
            _ when Has(text, "high school", "highschool", "bachillerato", "secundaria",
                "preparatoria") => EducationLevel.HighSchool,
            _ => null
        };
    }

    private static bool Has(string text, params string[] phrases) =>
        phrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));
}

namespace BuildCv.Application.Resumes;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// The section a heading line names. Only the first five are EXTRACTED; the rest are recognised so they
// act as boundaries — a "Projects" heading must still stop the Skills section even though nothing here
// reads projects — without their content bleeding into the section above them.
public enum SectionKind
{
    // The implicit region before the first recognised heading: name, contact details, sometimes a
    // summary with no heading. Not a heading itself.
    Header,
    Summary,
    Experience,
    Education,
    Skills,
    Languages,

    // A heading the parser knows by name but does not extract (projects, certificates, references, ...).
    // A boundary, its body skipped, and NOT warned about — the candidate keeps filling those in by hand,
    // exactly as they did before this PR.
    OtherRecognised,

    // A heading-shaped line the parser does not recognise. A boundary whose body is skipped, and WARNED
    // about, so the sections on either side of it survive and the candidate is told what was ignored.
    Unknown
}

/// <summary>
/// Recognises bilingual (Spanish / English) CV section headings, accent-insensitively.
/// </summary>
/// <remarks>
/// Spanish CVs mix both languages, and whether a PDF preserves accents depends on its encoding — the same
/// heading arrives as "Educación" from one export and "Educacion" from another. So every line is
/// normalised (lower-cased, stripped of diacritics and a trailing colon, whitespace collapsed) before it
/// is matched, and the dictionary keys are stored already normalised. Matching "Educación" and
/// "Educacion" to different sections would split one CV's education across two places.
/// </remarks>
public static class ResumeSectionHeadings
{
    // Declared before Headings on purpose: BuildHeadings normalises each phrase, which uses Whitespace,
    // and static field initialisers run top to bottom — the regex must exist first.
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // Keys are ALREADY normalised (no accents, lower case): the values these were derived from are the
    // headings a Spanish- or English-speaking candidate actually writes.
    private static readonly IReadOnlyDictionary<string, SectionKind> Headings = BuildHeadings();

    /// <summary>
    /// Normalises a line for heading comparison: trims, drops a trailing colon, strips diacritics,
    /// lower-cases and collapses internal whitespace. The value a candidate typed is never touched — only
    /// this comparison key is.
    /// </summary>
    public static string Normalize(string line)
    {
        var trimmed = line.Trim().TrimEnd(':').Trim();
        var decomposed = trimmed.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        var stripped = builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return Whitespace.Replace(stripped, " ").Trim();
    }

    /// <summary>
    /// Classifies a line as a section heading. Returns <c>null</c> when the line is body content, not a
    /// heading at all.
    /// </summary>
    /// <remarks>
    /// A recognised heading (dictionary hit) wins first. Otherwise a bare "Label:" line — letters and
    /// spaces terminated by a single colon and nothing after it — is reported as
    /// <see cref="SectionKind.Unknown"/> so it still closes the section above it. That rule is
    /// deliberately NARROW: an all-caps or title-case shape catches real content too — <c>AWS</c>,
    /// <c>SQL Server</c> and <c>Machine Learning</c> are skills, not sections — and a false heading splits
    /// a real section in half, which is worse than letting an unrecognised section's body bleed into the
    /// neighbour above it (where it stays visible and deletable on the review screen). A lone trailing
    /// colon is the one shape a skill or a line of prose almost never has.
    /// </remarks>
    public static SectionKind? Classify(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return null;

        var normalized = Normalize(trimmed);
        if (normalized.Length == 0)
            return null;

        if (Headings.TryGetValue(normalized, out var kind))
            return kind;

        return LabelHeading.IsMatch(trimmed) ? SectionKind.Unknown : null;
    }

    // A bare "Something:" label line — letters and spaces, one terminating colon, at most ~30 characters.
    // No digits (a date line is not a heading), no content after the colon (that would be "Field: value").
    private static readonly Regex LabelHeading = new(@"^\p{L}[\p{L} ]{0,29}:$", RegexOptions.Compiled);

    private static Dictionary<string, SectionKind> BuildHeadings()
    {
        var headings = new Dictionary<string, SectionKind>(StringComparer.Ordinal);

        void Add(SectionKind kind, params string[] phrases)
        {
            foreach (var phrase in phrases)
                headings[Normalize(phrase)] = kind;
        }

        Add(SectionKind.Summary,
            "Summary", "Professional Summary", "Profile", "Professional Profile", "About", "About Me",
            "Objective", "Career Objective",
            "Perfil", "Perfil Profesional", "Resumen", "Resumen Profesional", "Sobre Mi", "Acerca De Mi",
            "Objetivo", "Extracto", "Perfil Personal");

        Add(SectionKind.Experience,
            "Experience", "Work Experience", "Professional Experience", "Employment", "Employment History",
            "Work History",
            "Experiencia", "Experiencia Laboral", "Experiencia Profesional", "Experiencia De Trabajo",
            "Historial Laboral", "Trayectoria Profesional");

        Add(SectionKind.Education,
            "Education", "Academic Background", "Academic Education",
            "Educacion", "Formacion", "Formacion Academica", "Estudios", "Formacion Y Educacion");

        Add(SectionKind.Skills,
            "Skills", "Technical Skills", "Hard Skills", "Core Skills", "Key Skills", "Competencies",
            "Technologies",
            "Habilidades", "Habilidades Tecnicas", "Aptitudes", "Competencias", "Conocimientos",
            "Conocimientos Tecnicos", "Tecnologias");

        Add(SectionKind.Languages,
            "Languages", "Language Skills",
            "Idiomas", "Lenguas");

        Add(SectionKind.OtherRecognised,
            "Projects", "Personal Projects", "Certificates", "Certifications", "Awards", "Honors",
            "Publications", "Interests", "Hobbies", "References", "Volunteer", "Volunteering", "Contact",
            "Proyectos", "Certificaciones", "Certificados", "Premios", "Reconocimientos", "Distinciones",
            "Publicaciones", "Intereses", "Aficiones", "Referencias", "Voluntariado", "Contacto");

        return headings;
    }
}

namespace BuildCv.Application.Resumes;

using System.Text.RegularExpressions;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;

/// <summary>
/// Turns the raw text of a CV into a best-effort <see cref="ResumeDraft"/> plus the
/// <see cref="DraftConfidence"/> that travels beside it. Pure and deterministic — text in, proposal out,
/// no I/O and no domain construction — so it is unit-testable on plain strings and the corpus can drive
/// the exact same code the endpoint does.
/// </summary>
/// <remarks>
/// <para>
/// The governing rule is the whole PR's: a field the parser is unsure about arrives EMPTY and FLAGGED
/// (<see cref="FieldConfidence.NotExtracted"/>), never guessed and silent. Nothing here invents a value
/// the source does not contain — no defaulting a missing skill level, no assuming an experience type, no
/// inferring an end date from "Present". Absent is honest, and an empty field costs the candidate ten
/// seconds; a wrong one they do not notice corrupts their score invisibly.
/// </para>
/// <para>
/// A DATE ARRIVES AT THE PRECISION ITS SOURCE STATED, which is that rule rather than an exception to it:
/// "June 2015" becomes <c>2015-06</c> and never <c>2015-06-01</c>, because the draft's date fields now
/// hold a month or a year as readily as a day (see <c>PartialDate</c>). Before that was possible the same
/// rule left the field empty and flagged, which was honest and cost the candidate a re-type on almost
/// every job — month/year being the dominant format on real CVs.
/// </para>
/// <para>
/// It extracts contact details, skills, languages and the date/organisation skeleton of experience and
/// education — the fields a rule can reach with acceptable accuracy. Projects, certificates, awards,
/// publications, interests and references are left for the candidate to fill, exactly as before this PR:
/// low-confidence extraction there would be pure risk with little reward.
/// </para>
/// </remarks>
public static class ResumeTextParser
{
    public const string TwoColumnWarning =
        "This looks like a two-column layout. The columns may have been read in the wrong order, so the "
        + "extracted text — and everything below — can be unreliable. Review every field carefully, or "
        + "paste the text yourself instead.";

    public static string UnrecognisedSectionWarning(string heading) =>
        $"The section \"{heading}\" was not recognised, so its contents were skipped. Add anything from it "
        + "by hand.";

    private static readonly Regex Email = new(
        @"[\p{L}0-9._%+\-]+@[\p{L}0-9.\-]+\.\p{L}{2,}", RegexOptions.Compiled);

    // A phone-shaped run: an optional +, then digits and the usual separators. The digit-count filter in
    // TryExtractPhone is what keeps a year range like "2019 - 2021" from being read as a phone number.
    private static readonly Regex Phone = new(
        @"(?<!\w)\+?\d[\d\s().\-]{5,}\d(?!\w)", RegexOptions.Compiled);

    private static readonly Regex Url = new(
        @"(?:https?://|www\.)[^\s]+|[\p{L}0-9\-]+\.(?:com|net|org|io|dev|es|me|co|ai|app|info|tech)(?:/[^\s]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Splits a skills or technologies line into items: commas, semicolons, pipes, middots and bullets.
    private static readonly Regex ItemSeparators = new(@"[,;|/•·‣▪◦\t]+", RegexOptions.Compiled);

    // A leading bullet glyph or dash the candidate typed, stripped before an item or context line is read.
    private static readonly Regex LeadingBullet = new(@"^[\s\-–—*•·‣▪◦]+", RegexOptions.Compiled);

    public static ResumeDraftProposal Parse(
        string text,
        ColumnLayout layout = ColumnLayout.Unknown,
        IReadOnlyList<string>? documentWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var fields = new List<FieldProvenance>();
        var warnings = new List<string>();
        if (documentWarnings is not null)
            warnings.AddRange(documentWarnings);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sections = Segment(lines, warnings);

        var contact = BuildContact(sections.Header, text, sections.Body(SectionKind.Summary), fields);
        var skills = BuildSkills(sections.Body(SectionKind.Skills), fields);
        var languages = BuildLanguages(sections.Body(SectionKind.Languages), fields);
        var experiences = BuildExperiences(sections.Body(SectionKind.Experience), fields);
        var educations = BuildEducations(sections.Body(SectionKind.Education), fields);

        // Two columns are WARNED about, loudly and first, never silently interleaved: an interleaved read
        // is plausible, wrong prose, exactly the invisible corruption this phase exists to prevent.
        if (layout == ColumnLayout.Multiple)
            warnings.Insert(0, TwoColumnWarning);

        var draft = new ResumeDraft(
            Contact: contact,
            Experiences: experiences,
            Educations: educations,
            Skills: skills,
            Languages: languages);

        var overall = DetermineOverall(layout, fields);
        return new ResumeDraftProposal(draft, new DraftConfidence(overall, fields, warnings));
    }

    // ------------------------------------------------------------------ segmentation

    private sealed class Sections
    {
        public List<string> Header { get; } = [];
        private Dictionary<SectionKind, List<string>> Bodies { get; } = [];

        public List<string> BodyFor(SectionKind kind)
        {
            if (!Bodies.TryGetValue(kind, out var body))
                Bodies[kind] = body = [];
            return body;
        }

        public IReadOnlyList<string> Body(SectionKind kind) =>
            Bodies.TryGetValue(kind, out var body) ? body : [];
    }

    private static Sections Segment(IReadOnlyList<string> lines, List<string> warnings)
    {
        var sections = new Sections();
        var current = SectionKind.Header;
        var warned = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var kind = ResumeSectionHeadings.Classify(line);

            // A bare "Label:" line (SectionKind.Unknown — matched by the label-shape fallback in Classify,
            // never by the heading dictionary) INSIDE a date-anchored section is almost always an in-entry
            // sub-label — "Responsibilities:", "Logros:", "Funciones:", "Objetivo Profesional:" — not a new
            // top-level section. Resetting the section to Unknown there would drop every line after it until
            // the next recognised heading, swallowing a whole second job whose org, title and date parse
            // fine. So inside Experience / Education we demote the label to body content: DatedEntries
            // anchors on dates and trims context to the last two lines, so a stray sub-label does no harm
            // while the dated entries beneath it still parse. The unrecognised-section WARNING is then
            // reserved for genuine top-level headings (a label seen in the header, summary, skills or
            // languages context), where nothing dated is at risk below it.
            if (kind == SectionKind.Unknown && current is SectionKind.Experience or SectionKind.Education)
                kind = null;

            if (kind is null)
            {
                if (current == SectionKind.Header)
                    sections.Header.Add(line);
                else if (current is SectionKind.Summary or SectionKind.Experience
                         or SectionKind.Education or SectionKind.Skills or SectionKind.Languages)
                    sections.BodyFor(current).Add(line);
                // OtherRecognised and Unknown bodies are deliberately dropped.
                continue;
            }

            current = kind.Value;
            if (kind == SectionKind.Unknown && warned.Add(ResumeSectionHeadings.Normalize(line)))
                warnings.Add(UnrecognisedSectionWarning(line.Trim().TrimEnd(':').Trim()));
        }

        return sections;
    }

    // ------------------------------------------------------------------ contact

    private static ContactDraft BuildContact(
        IReadOnlyList<string> header, string fullText, IReadOnlyList<string> summary, List<FieldProvenance> fields)
    {
        // Email, phone and URL are unambiguous patterns searched over the WHOLE document, because a CV
        // may put them under a "Contacto" heading rather than in the unlabelled header. Name and location
        // are POSITIONAL guesses and only read from the header, where the candidate's own details sit.
        var email = Record(fields, "contact.email", FirstEmail(fullText), FieldConfidence.High);
        var phone = Record(fields, "contact.phoneNumber", FirstPhone(fullText), FieldConfidence.Medium);
        var website = Record(fields, "contact.website", FirstUrl(fullText), FieldConfidence.Medium);
        var fullName = Record(fields, "contact.fullName", FirstName(header), FieldConfidence.Medium);
        var location = Record(fields, "contact.location", FirstLocation(header), FieldConfidence.Low);

        var summaryText = summary.Count == 0
            ? null
            : string.Join(" ", summary.Select(line => line.Trim()).Where(line => line.Length > 0));
        var summaryValue = Record(
            fields, "contact.summary", string.IsNullOrWhiteSpace(summaryText) ? null : summaryText,
            FieldConfidence.Medium);

        return new ContactDraft(
            FullName: fullName,
            Email: email,
            PhoneNumber: phone,
            Location: location,
            Website: website,
            Summary: summaryValue);
    }

    private static string? FirstEmail(string text)
    {
        var match = Email.Match(text);
        return match.Success ? match.Value : null;
    }

    private static string? FirstPhone(string text)
    {
        foreach (Match match in Phone.Matches(text))
        {
            var digits = match.Value.Count(char.IsDigit);
            var hasPlus = match.Value.TrimStart().StartsWith('+');
            // Enough digits to be a phone rather than a year range or a short id. A leading + lowers the
            // bar to 7 (an international number written compactly); without one, demand 9.
            if (hasPlus ? digits >= 7 : digits >= 9)
                return match.Value.Trim();
        }

        return null;
    }

    private static string? FirstUrl(string text)
    {
        foreach (Match match in Url.Matches(text))
        {
            // Skip an email's domain: "example.com" inside "john@example.com" matches the URL shape but
            // is preceded by '@'. The website is the first URL that is not the tail of an address.
            if (match.Value.Contains('@'))
                continue;
            if (match.Index > 0 && text[match.Index - 1] == '@')
                continue;

            return match.Value.TrimEnd('.', ',', ')');
        }

        return null;
    }

    private static string? FirstName(IReadOnlyList<string> header)
    {
        foreach (var raw in header)
        {
            var line = raw.Trim();
            if (line.Length is 0 or > 60)
                continue;
            if (line.Contains('@') || Url.IsMatch(line) || line.Any(char.IsDigit))
                continue;

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length is < 1 or > 4)
                continue;
            if (words.All(IsNameWord))
                return line;
        }

        return null;
    }

    // A name token: starts with an upper-case letter, the rest letters or an inner hyphen/apostrophe/dot.
    private static bool IsNameWord(string word)
    {
        if (word.Length == 0 || !char.IsUpper(word[0]))
            return false;
        return word.All(character => char.IsLetter(character) || character is '-' or '\'' or '.');
    }

    // "City, Country" / "Madrid, España": one comma, letters and spaces either side, no digits, no @.
    private static string? FirstLocation(IReadOnlyList<string> header)
    {
        foreach (var raw in header)
        {
            var line = raw.Trim();
            if (line.Length is 0 or > 60 || line.Contains('@') || line.Any(char.IsDigit) || Url.IsMatch(line))
                continue;

            var parts = line.Split(',');
            if (parts.Length != 2)
                continue;

            bool SideIsPlace(string side)
            {
                var words = side.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return words.Length is >= 1 and <= 3 && words.All(w => w.All(c => char.IsLetter(c) || c is '-'));
            }

            if (SideIsPlace(parts[0]) && SideIsPlace(parts[1]))
                return line;
        }

        return null;
    }

    // ------------------------------------------------------------------ skills

    private static IReadOnlyList<SkillDraft>? BuildSkills(
        IReadOnlyList<string> body, List<FieldProvenance> fields)
    {
        var skills = new List<SkillDraft>();
        foreach (var line in body)
        {
            foreach (var token in ItemSeparators.Split(LeadingBullet.Replace(line, string.Empty)))
            {
                var name = token.Trim();
                if (name.Length is 0 or > 60)
                    continue;

                var level = LevelWords.SkillLevelIn(name, out var withoutLevel);
                var cleanName = string.IsNullOrWhiteSpace(withoutLevel) ? name : withoutLevel;

                var index = skills.Count;
                fields.Add(new FieldProvenance($"skills[{index}].name", FieldConfidence.Medium, name));
                // Level is flagged either way, exactly like Record does for a contact field: High with the
                // source token when a parenthetical named it, NotExtracted when the skill states no level.
                // The draft's Level is null in the second case, so "absent and flagged" stays one fact.
                fields.Add(level is not null
                    ? new FieldProvenance($"skills[{index}].level", FieldConfidence.High, name)
                    : new FieldProvenance($"skills[{index}].level", FieldConfidence.NotExtracted));

                skills.Add(new SkillDraft(Name: cleanName, Level: level?.ToString()));
            }
        }

        return skills.Count == 0 ? null : skills;
    }

    // ------------------------------------------------------------------ languages

    private static IReadOnlyList<LanguageDraft>? BuildLanguages(
        IReadOnlyList<string> body, List<FieldProvenance> fields)
    {
        var languages = new List<LanguageDraft>();
        foreach (var raw in body)
        {
            var line = LeadingBullet.Replace(raw, string.Empty).Trim();
            if (line.Length == 0)
                continue;

            var separator = line.IndexOfAny([':', '-', '–', '—', '(']);
            var name = (separator < 0 ? line : line[..separator]).Trim();
            if (name.Length is 0 or > 40)
                continue;

            var fluency = separator < 0
                ? null
                : line[(separator + 1)..].Trim().TrimEnd(')').Trim();
            var fluencyValue = string.IsNullOrWhiteSpace(fluency) ? null : fluency;

            var level = fluencyValue is null ? null : LevelWords.LanguageLevelIn(fluencyValue);

            var index = languages.Count;
            fields.Add(new FieldProvenance($"languages[{index}].name", FieldConfidence.Medium, name));
            // Fluency is the raw free text after the separator and is written onto the draft as-is, so it
            // must carry its own provenance — a populated field with no confidence entry is exactly the gap
            // "absent and flagged" forbids. Medium when present (a positional split that is usually right),
            // NotExtracted when the line named only the language. Level is the enum mapped from that same
            // fluency text: High when it maps, NotExtracted (draft null) when it does not.
            fields.Add(fluencyValue is not null
                ? new FieldProvenance($"languages[{index}].fluency", FieldConfidence.Medium, fluencyValue)
                : new FieldProvenance($"languages[{index}].fluency", FieldConfidence.NotExtracted));
            fields.Add(level is not null
                ? new FieldProvenance($"languages[{index}].level", FieldConfidence.High, fluencyValue)
                : new FieldProvenance($"languages[{index}].level", FieldConfidence.NotExtracted));

            languages.Add(new LanguageDraft(Name: name, Fluency: fluencyValue, Level: level?.ToString()));
        }

        return languages.Count == 0 ? null : languages;
    }

    // ------------------------------------------------------------------ experience / education

    private static IReadOnlyList<ExperienceDraft>? BuildExperiences(
        IReadOnlyList<string> body, List<FieldProvenance> fields)
    {
        var experiences = new List<ExperienceDraft>();
        foreach (var (context, range) in DatedEntries(body))
        {
            var index = experiences.Count;
            var (position, organization) = SplitContext(context);
            var path = $"experiences[{index}]";

            RecordContext(fields, $"{path}.position", position);
            RecordContext(fields, $"{path}.organization", organization);
            var (start, end) = RecordRange(fields, path, range);
            // Type is never guessed: no CV states "Professional" vs "Volunteer" in a machine-readable way,
            // and assuming one inflates or deflates the experience the candidate reviews.
            fields.Add(new FieldProvenance($"{path}.type", FieldConfidence.NotExtracted));

            experiences.Add(new ExperienceDraft(
                Type: null, Organization: organization, Position: position, Start: start, End: end));
        }

        return experiences.Count == 0 ? null : experiences;
    }

    private static IReadOnlyList<EducationDraft>? BuildEducations(
        IReadOnlyList<string> body, List<FieldProvenance> fields)
    {
        var educations = new List<EducationDraft>();
        foreach (var (context, range) in DatedEntries(body))
        {
            var index = educations.Count;
            var (degree, institution, level) = SplitEducationContext(context);
            var path = $"educations[{index}]";

            RecordContext(fields, $"{path}.institution", institution);
            // Degree and level are flagged either way, like every other field the parser reasons about:
            // present with their source, or NotExtracted paired with a null draft value. Emitting nothing
            // when absent is the gap "absent and flagged" forbids.
            fields.Add(degree is not null
                ? new FieldProvenance($"{path}.degree", FieldConfidence.Low, degree)
                : new FieldProvenance($"{path}.degree", FieldConfidence.NotExtracted));
            fields.Add(level is not null
                ? new FieldProvenance($"{path}.level", FieldConfidence.High, degree)
                : new FieldProvenance($"{path}.level", FieldConfidence.NotExtracted));
            var (start, end) = RecordRange(fields, path, range);

            educations.Add(new EducationDraft(
                Institution: institution, Degree: degree, Start: start, End: end, Level: level?.ToString()));
        }

        return educations.Count == 0 ? null : educations;
    }

    // One entry per line carrying a date range, with the (up to two) non-empty, non-date lines
    // immediately above it as its context. Lines with no date are not turned into entries — a guessed
    // organisation with no date to anchor it is more noise than help.
    private static IEnumerable<(IReadOnlyList<string> Context, CvDateRange Range)> DatedEntries(
        IReadOnlyList<string> body)
    {
        var lastConsumed = -1;
        for (var i = 0; i < body.Count; i++)
        {
            var range = CvDateParser.FindRange(body[i]);
            if (range is null)
                continue;

            var context = new List<string>();
            for (var j = lastConsumed + 1; j < i; j++)
            {
                var line = LeadingBullet.Replace(body[j], string.Empty).Trim();
                if (line.Length > 0 && CvDateParser.FindRange(body[j]) is null)
                    context.Add(line);
            }

            var trimmedContext = context.Count > 2 ? context.GetRange(context.Count - 2, 2) : context;
            yield return (trimmedContext, range);
            lastConsumed = i;
        }
    }

    // Two context lines: the first is read as the position/degree, the second as the organisation — the
    // "Title\nCompany" order most CVs use. One line with a separator is split; one plain line becomes the
    // organisation and leaves the position blank and flagged. All of this is a low-confidence guess.
    private static (string? First, string? Second) SplitContext(IReadOnlyList<string> context)
    {
        if (context.Count >= 2)
            return (context[0], context[1]);
        if (context.Count == 0)
            return (null, null);

        var line = context[0];
        var separator = line.IndexOfAny(['—', '–', '|']);
        if (separator > 0)
            return (line[..separator].Trim(), line[(separator + 1)..].Trim());

        foreach (var word in new[] { " at ", " en " })
        {
            var at = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
                return (line[..at].Trim(), line[(at + word.Length)..].Trim());
        }

        return (null, line);
    }

    private static (string? Degree, string? Institution, EducationLevel? Level) SplitEducationContext(
        IReadOnlyList<string> context)
    {
        var (first, second) = SplitContext(context);

        // Whichever context line names a degree keyword is the degree; the other is the institution.
        var candidates = new[] { first, second };
        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i] is { } candidate && LevelWords.EducationLevelIn(candidate) is { } level)
            {
                var institution = i == 0 ? second : first;
                return (candidate, institution, level);
            }
        }

        // No degree keyword: the first context line reads as a free-text degree, the second as the
        // institution. A single plain line comes back from SplitContext as (null, line), so the
        // institution falls back to it and the degree stays blank.
        return (first, second ?? first, null);
    }

    // ------------------------------------------------------------------ provenance helpers

    private static string? Record(
        List<FieldProvenance> fields, string path, string? value, FieldConfidence confidence)
    {
        fields.Add(value is null
            ? new FieldProvenance(path, FieldConfidence.NotExtracted)
            : new FieldProvenance(path, confidence, value));
        return value;
    }

    private static void RecordContext(List<FieldProvenance> fields, string path, string? value) =>
        fields.Add(value is null
            ? new FieldProvenance(path, FieldConfidence.NotExtracted)
            : new FieldProvenance(path, FieldConfidence.Low, value));

    private static (string? Start, string? End) RecordRange(
        List<FieldProvenance> fields, string path, CvDateRange range)
    {
        var start = DateProvenance(fields, $"{path}.start", range.Start);

        string? end;
        if (range.EndIsPresent)
        {
            // "Present" is recognised but the end stays blank: filling today's date invents a value.
            fields.Add(new FieldProvenance($"{path}.end", FieldConfidence.NotExtracted, "Present"));
            end = null;
        }
        else if (range.End is null)
        {
            fields.Add(new FieldProvenance($"{path}.end", FieldConfidence.NotExtracted));
            end = null;
        }
        else
        {
            end = DateProvenance(fields, $"{path}.end", range.End);
        }

        return (start, end);
    }

    private static string? DateProvenance(List<FieldProvenance> fields, string path, CvDate date)
    {
        if (date.Value is not null)
        {
            fields.Add(new FieldProvenance(path, FieldConfidence.Medium, date.SourceText));
            return date.Value;
        }

        // Recognised as a date attempt and resolving to nothing — a two-digit year, an impossible
        // calendar date, a month outside 1..12. Blank and flagged, with the raw snippet so the candidate
        // can complete it. A month-and-year or a bare year no longer lands here: it now carries its own
        // precision and arrives above with a value, which is what CvDateParser's remarks describe.
        fields.Add(new FieldProvenance(path, FieldConfidence.NotExtracted, date.SourceText));
        return null;
    }

    // ------------------------------------------------------------------ overall

    private static OverallConfidence DetermineOverall(ColumnLayout layout, IReadOnlyList<FieldProvenance> fields)
    {
        // A two-column read is untrustworthy end to end: say so, regardless of how much was extracted.
        if (layout == ColumnLayout.Multiple)
            return OverallConfidence.Low;

        var extracted = fields.Count(f => f.Confidence != FieldConfidence.NotExtracted);
        var strong = fields.Count(f => f.Confidence >= FieldConfidence.Medium);

        if (extracted == 0)
            return OverallConfidence.Low;
        return strong >= 5 ? OverallConfidence.High : OverallConfidence.Medium;
    }
}

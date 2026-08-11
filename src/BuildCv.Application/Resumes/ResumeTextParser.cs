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

    // A line that genuinely opens with a bullet MARKER and has content after it. Deliberately stricter
    // than LeadingBullet, whose character class includes \s and so matches any indented line.
    private static readonly Regex BulletLine = new(@"^\s*[\-–—*•·‣▪◦]+\s*\S", RegexOptions.Compiled);

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
        // Read before the phone is recorded, because it is the only evidence in the document that can
        // turn a national number into a dialable one. Recorded in its original position below so the
        // provenance list keeps the order a review screen renders.
        var locationValue = FirstLocation(header);

        var email = Record(fields, "contact.email", FirstEmail(fullText), FieldConfidence.High);
        var phone = Record(
            fields, "contact.phoneNumber", FirstPhone(fullText), FieldConfidence.Medium,
            value => PhoneSuggestion(value, locationValue));
        var website = Record(
            fields, "contact.website", FirstUrl(fullText), FieldConfidence.Medium, WebsiteSuggestion);
        var fullName = Record(fields, "contact.fullName", FirstName(header), FieldConfidence.Medium);
        var location = Record(fields, "contact.location", locationValue, FieldConfidence.Low);

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

    /// <summary>
    /// Whether <paramref name="previous"/> stopped mid-sentence, so the next unmarked line continues it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stated as what ENDS a sentence, not as what continues one.</b> The first version listed the
    /// characters a continuation could follow — lowercase, comma, hyphen — and a bullet ending
    /// <c>"… — 70%"</c> fell straight through the gap, taking the rest of that job's achievements with
    /// it. Any list of allowed characters is a list somebody's CV will step outside; there are only four
    /// ways a sentence ends.
    /// </para>
    /// <para>
    /// This is permissive on purpose, and what makes it safe is the caller: the highlight loop already
    /// stops at the next date line, which is where a real entry begins. The residual is a layout that
    /// puts the role on a line of its own with no date — there, a bullet with no full stop would swallow
    /// the role. That degrades to the empty-context path, which reads the role from the date line or the
    /// line ahead, rather than to a wrong value.
    /// </para>
    /// </remarks>
    private static bool ContinuesTheLineBefore(string? previous)
    {
        var text = previous?.TrimEnd();
        if (string.IsNullOrEmpty(text))
            return false;

        return text[^1] is not ('.' or '!' or '?' or ':');
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
    /// <summary>
    /// The candidate's location, read from the header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each pipe-separated segment is tested on its own</b>, because the modern one-line header puts
    /// everything on one row:
    /// </para>
    /// <code>
    /// Cali, Valle del Cauca, Colombia | hi@cristianarellano.com | 310 4580645
    /// </code>
    /// <para>
    /// Tested whole, that line is disqualified by the first guard it meets — it contains an <c>@</c>, and
    /// digits, and a URL-shaped run — so the location was simply not extracted. Measured on a real CV,
    /// where it also cost the phone its country hint and therefore its suggestion: two fields the
    /// candidate had to fill because a third one was read as a single string.
    /// </para>
    /// <para>
    /// Two or three comma parts, not exactly two. "Cali, Colombia" and "Cali, Valle del Cauca, Colombia"
    /// are both ordinary, and a rule that admits only the shorter one silently prefers CVs written the
    /// way it expects.
    /// </para>
    /// </remarks>
    private static string? FirstLocation(IReadOnlyList<string> header)
    {
        foreach (var raw in header)
        {
            foreach (var segment in raw.Split('|', '·', '•'))
            {
                var line = segment.Trim();
                if (line.Length is 0 or > 60 || line.Contains('@') || line.Any(char.IsDigit) || Url.IsMatch(line))
                    continue;

                if (LooksLikeAPlace(line))
                    return line;
            }
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
        foreach (var (context, range, highlights) in DatedEntries(body))
        {
            var index = experiences.Count;
            var (position, organization) = SplitContext(context);
            var path = $"experiences[{index}]";

            RecordContext(fields, $"{path}.position", position);
            RecordContext(fields, $"{path}.organization", organization);
            var (start, end) = RecordRange(fields, path, range);
            // PROFESSIONAL, INFERRED FROM WHERE THE ENTRY WAS FOUND rather than from its words. This used
            // to be left null on the reasoning that no CV states "Professional" vs "Volunteer" in a
            // machine-readable way — true of the TEXT, and it missed that the section heading already
            // said it. These entries come from the body of a heading classified as Experience.
            //
            // Leaving it null was not neutral, it was expensive: `ResumeDraftValidator` requires the type,
            // so every imported entry arrived as a blocking error. Measured on a real import: nine
            // experiences, nine mandatory clicks on a two-value enum, before anything could be created.
            //
            // MEDIUM, the same confidence every other positional read here carries, so the review screen
            // marks it CHECK and the candidate sees a value they can correct rather than a hole they must
            // fill. The validator stays strict — the draft now states the type, so nothing is defaulted
            // behind anybody's back.
            //
            // A volunteering section is a different matter and is NOT covered: there is no
            // SectionKind.Volunteer, so those bodies are skipped entirely today and the data is lost.
            // That is a gap worth closing, and closing it is what would make this inference wrong.
            fields.Add(new FieldProvenance(
                $"{path}.type", FieldConfidence.Medium, nameof(ExperienceType.Professional)));
            // Medium, on the same reasoning as every other positional read here: the TEXT is verbatim, but
            // that these bullets belong to THIS role is inferred from them sitting under its date line.
            // Absent and flagged when the document listed none, never silently missing.
            fields.Add(highlights.Count > 0
                ? new FieldProvenance($"{path}.highlights", FieldConfidence.Medium, string.Join(" | ", highlights))
                : new FieldProvenance($"{path}.highlights", FieldConfidence.NotExtracted));

            experiences.Add(new ExperienceDraft(
                Type: nameof(ExperienceType.Professional),
                Organization: organization, Position: position, Start: start, End: end,
                Highlights: highlights.Count == 0 ? null : highlights));
        }

        return experiences.Count == 0 ? null : experiences;
    }

    private static IReadOnlyList<EducationDraft>? BuildEducations(
        IReadOnlyList<string> body, List<FieldProvenance> fields)
    {
        var educations = new List<EducationDraft>();
        // Highlights are discarded here — EducationDraft has no such field. The consumption still matters:
        // it keeps a bullet under a degree from being read as the next degree's institution.
        foreach (var (context, range, _) in DatedEntries(body))
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
    // immediately above it as its context, and the bullet lines immediately BELOW it as its highlights.
    // Lines with no date are not turned into entries — a guessed organisation with no date to anchor it
    // is more noise than help.
    //
    // Reading downwards is not a new feature bolted on; it repairs the entry that follows. Before this,
    // every line under an anchor fell into the NEXT entry's context window, was bullet-stripped by the
    // loop below, and — because the window keeps the last two lines — a role written as
    //
    //     Senior Engineer            <- context of entry 1
    //     2019 - 2024                <- anchor 1
    //     - Cut settlement time 40%  <- became context of entry 2
    //     Junior Engineer            <- became context of entry 2
    //     2015 - 2019                <- anchor 2
    //
    // produced entry 2 with Position = "Cut settlement time 40%" and Organization = "Junior Engineer".
    // Consuming those lines here is what stops one job's achievements from being read as the next job's
    // title.
    private static IEnumerable<(IReadOnlyList<string> Context, CvDateRange Range, IReadOnlyList<string?> Highlights)> DatedEntries(
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

            // THE SAME-LINE LAYOUT, and it is the common one rather than an edge case. Real CVs put the
            // role, the employer and the period on ONE line with the period pushed to the right margin:
            //
            //     Backend Engineer — Shoppipai, Independent Development – Cali, Colombia   Dec 2025 – present
            //
            // Looking backwards finds nothing here, because the context was never on a line of its own.
            // Measured against our user's actual CV: every one of its entries is this shape, and every one
            // arrived with an empty employer and an empty role — the "Value is required" pair on the review
            // screen that prompted this work. Dropping those as orphans, which the guard below does, would
            // have thrown the whole work history away instead of showing it half-read.
            //
            // CvDateParser.WithoutDates does the stripping, so the grammar of a date stays in one file.
            if (trimmedContext.Count == 0)
            {
                var sameLine = CvDateParser.WithoutDates(body[i]);
                if (sameLine.Length > 0)
                    trimmedContext = [sameLine];
            }

            // THE DATE-FIRST LAYOUT, which looking backwards alone cannot read. Plenty of CVs put the
            // period above the role:
            //
            //     2019 - 2021
            //     Senior Developer, Globant
            //
            // Searching only behind finds nothing, and the title then becomes context for the NEXT date
            // line — so one such block corrupts two entries: this one arrives nameless and the next one
            // wears the wrong job. Taking the line ahead when there is nothing behind reads both layouts,
            // and it is consumed below so it cannot be claimed twice.
            var titleAhead = -1;
            if (trimmedContext.Count == 0 && i + 1 < body.Count
                && !BulletLine.IsMatch(body[i + 1])
                && CvDateParser.FindRange(body[i + 1]) is null)
            {
                var ahead = LeadingBullet.Replace(body[i + 1], string.Empty).Trim();
                if (ahead.Length > 0)
                {
                    trimmedContext = [ahead];
                    titleAhead = i + 1;
                }
            }

            // BulletLine, not LeadingBullet: the latter is `^[\s...]+`, so it matches on indentation
            // alone and would swallow an indented job title. A highlight has to carry an actual marker.
            var highlights = new List<string?>();
            var k = (titleAhead >= 0 ? titleAhead : i) + 1;
            for (; k < body.Count && CvDateParser.FindRange(body[k]) is null; k++)
            {
                // A BLANK LINE BETWEEN BULLETS IS SPACING, NOT A BOUNDARY — and treating it as one is
                // what defeated the two previous attempts at this. PdfPig emits an empty line between
                // some bullets and not others:
                //
                //     • Configured enterprise security: … rate limiting.
                //                                                          <- empty
                //     • Implemented semantic search …
                //
                // Stopping there left the remaining bullets unconsumed, and the last two of them became
                // the next entry's job title and employer. Consuming the blank and carrying on costs
                // nothing: the loop still ends at the next date line, which is the real boundary.
                if (body[k].Trim().Length == 0)
                    continue;

                if (BulletLine.IsMatch(body[k]))
                {
                    // Past the cap the line is still CONSUMED, just not kept. Stopping the loop instead
                    // would hand the 51st bullet to the next entry as its job title, which is the exact
                    // corruption this block exists to prevent.
                    if (highlights.Count >= ResumeDraftLimits.TextItems)
                        continue;

                    var text = LeadingBullet.Replace(body[k], string.Empty).Trim();
                    if (text.Length > 0)
                        highlights.Add(text);

                    continue;
                }

                // A BULLET THAT WRAPPED, WHICH IS MOST OF THEM. A PDF breaks a long achievement across
                // two lines and only the first carries the marker:
                //
                //     • Resolved L2/L3 escalations … coming from first-line support, against
                //       aggressive SLAs.
                //
                // The old loop stopped at the second line, so it stayed unconsumed and became CONTEXT for
                // the next dated entry — which is why entries two onward arrived with a fragment of the
                // previous job's achievements as their title and employer. Measured on a real CV: five of
                // six entries were wrong this way, and they read like data rather than like blanks, which
                // is the more dangerous failure of the two.
                //
                // THE SIGNAL IS THE PREVIOUS LINE ENDING MID-SENTENCE, not indentation.
                //
                // Indentation was the obvious choice and it does not survive: this parser reads what
                // PdfPigTextExtractor produces, and that is `ContentOrderTextExtractor`, which rebuilds
                // text from glyph positions in reading order and emits no leading whitespace. The
                // indentation is real in the PDF and real in `pdftotext -layout` — and absent from the
                // string this code actually receives. Verified the wrong way once already: a fix written
                // against `pdftotext` output shipped, deployed, and changed nothing.
                //
                // A wrapped line is one whose predecessor stopped in the middle of a sentence — ending in
                // a lowercase letter, a comma or a hyphen. That holds whatever the extractor does with
                // whitespace, because it is a property of the sentence rather than of the layout.
                // Requiring an existing highlight as well means a stray line before any bullet is still
                // left alone for the context reader.
                if (highlights.Count > 0 && ContinuesTheLineBefore(highlights[^1]))
                {
                    var continuation = body[k].Trim();
                    if (continuation.Length == 0)
                        continue;

                    var previous = highlights[^1] ?? string.Empty;

                    // A hyphen at the break is the PDF's word-splitting, not the candidate's punctuation:
                    // "Develop-" + "ment" is one word and joining with a space would invent two. The soft
                    // hyphen is the same case and is what real producers emit.
                    highlights[^1] = previous.EndsWith('-') || previous.EndsWith('­')
                        ? previous[..^1] + continuation
                        : $"{previous} {continuation}";

                    continue;
                }

                // Neither a bullet nor a continuation: it names whatever comes next, so leave it.
                break;
            }

            // A DATE WITH NOTHING NAMING IT IS NOT AN ENTRY. Nothing behind it, nothing usable ahead of
            // it — so there is no role, no employer, no degree, and no way for the candidate to fix it
            // either: the review screen would show two "Value is required" fields with nothing to put in
            // them, on a row they never wrote. Measured on a real import: one such row, blocking a
            // submit, beside nine genuine entries.
            //
            // It is still CONSUMED (lastConsumed moves), so its bullets cannot drift onto the next entry
            // and become somebody else's achievements. Dropping without consuming would trade a visible
            // junk row for an invisible corruption, which is the worse of the two.
            if (trimmedContext.Count == 0)
            {
                lastConsumed = k - 1;
                continue;
            }

            yield return (trimmedContext, range, highlights);
            lastConsumed = k - 1;
        }
    }

    // The tail of a one-comma line, when it is only a company's legal form. Matched whole and
    // case-insensitively, with any trailing period ignored, so "Inc." and "inc" both count while a real
    // employer named "Incognito" does not. Covers the forms this product's Spanish-speaking market
    // actually writes alongside the anglophone ones.
    private static readonly string[] LegalFormSuffixes =
    [
        "inc", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "plc", "llp", "lp",
        "gmbh", "ag", "bv", "nv", "ab", "as", "oy", "pty", "pte",
        "sa", "s a", "sas", "sl", "srl", "sac", "spa", "sapi", "sapi de cv", "de cv", "cv", "sabde cv",
        "sociedad anonima", "sociedad anónima", "eirl", "sca", "scs", "sc",
    ];

    private static bool IsLegalFormSuffix(string tail)
    {
        var normalized = tail.TrimEnd('.').Replace(".", string.Empty, StringComparison.Ordinal).Trim();
        return LegalFormSuffixes.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    // Two context lines: the first is read as the position/degree, the second as the organisation — the
    // "Title\nCompany" order most CVs use. One line with a separator is split; one plain line becomes the
    // organisation and leaves the position blank and flagged. All of this is a low-confidence guess.
    /// <summary>
    /// Whether <paramref name="text"/> reads as a place — "Cali, Colombia", "Popayán, Colombia".
    /// </summary>
    /// <remarks>
    /// The same shape <see cref="FirstLocation"/> recognises in the header, stated once so a line that
    /// ends in a location is read the same way wherever it appears.
    /// </remarks>
    private static bool LooksLikeAPlace(string text)
    {
        var parts = text.Split(',');
        if (parts.Length is < 2 or > 3)
            return false;

        return parts.All(part =>
        {
            var words = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length is >= 1 and <= 3
                && words.All(word => word.All(c => char.IsLetter(c) || c is '-'));
        });
    }

    /// <summary>
    /// Drops a trailing " – City, Country" from an entry's context line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Real CVs write the whole entry on one line and put the workplace's city last:
    /// </para>
    /// <code>
    /// IT Support &amp; Systems, CDA La Luna – Cali, Colombia
    /// </code>
    /// <para>
    /// The dash is tried before the comma below — correctly, for the far more common
    /// "Senior Engineer — Company" — so with the city still attached the split lands in the wrong place:
    /// the employer stays inside the role and <b>the city becomes the employer</b>. Measured on a real
    /// import: five of six entries had "Cali, Colombia" in the company field, which is not a gap the
    /// candidate can see is wrong at a glance.
    /// </para>
    /// <para>
    /// Only the LAST dash-separated segment is considered, and only when it reads as a place, so an
    /// employer whose own name contains a dash keeps it.
    /// </para>
    /// </remarks>
    private static string WithoutTrailingPlace(string line)
    {
        var cut = line.LastIndexOfAny(['—', '–', '-']);
        if (cut <= 0)
            return line;

        var tail = line[(cut + 1)..].Trim();
        return tail.Length > 0 && LooksLikeAPlace(tail) ? line[..cut].Trim() : line;
    }

    private static (string? First, string? Second) SplitContext(IReadOnlyList<string> context)
    {
        if (context.Count >= 2)
            return (context[0], context[1]);
        if (context.Count == 0)
            return (null, null);

        var line = WithoutTrailingPlace(context[0]);
        var separator = line.IndexOfAny(['—', '–', '|']);
        if (separator > 0)
            return (line[..separator].Trim(), line[(separator + 1)..].Trim());

        foreach (var word in new[] { " at ", " en " })
        {
            var at = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
                return (line[..at].Trim(), line[(at + word.Length)..].Trim());
        }

        // The comma is the most common separator on a real CV — "Senior Engineer, Remington Rand" — and
        // also the most ambiguous one, which is why it is tried LAST and guarded. A company name carries
        // commas of its own, and splitting "Acme, Inc." yields Position "Acme" / Organization "Inc.":
        // strictly worse than the untouched fallback below, which at least keeps the name whole.
        //
        // So: exactly one comma, and the tail must not be a legal-form suffix. Anything more ambiguous
        // than that falls through and stays honest — the position is left null and FLAGGED, which the
        // review screen shows as "please fill in" rather than as a value the candidate might not reread.
        var comma = line.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0 && line.IndexOf(',', comma + 1) < 0)
        {
            var head = line[..comma].Trim();
            var tail = line[(comma + 1)..].Trim();
            if (head.Length > 0 && tail.Length > 0 && !IsLegalFormSuffix(tail))
                return (head, tail);
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

    /// <param name="suggestion">
    /// Given the extracted value, a corrected one to offer as one click — or null when there is nothing
    /// safe to propose. Consulted only when a value was actually extracted: a field the parser never
    /// found has nothing to correct, and proposing a value there would be inventing one.
    /// </param>
    private static string? Record(
        List<FieldProvenance> fields,
        string path,
        string? value,
        FieldConfidence confidence,
        Func<string, string?>? suggestion = null)
    {
        fields.Add(value is null
            ? new FieldProvenance(path, FieldConfidence.NotExtracted)
            : new FieldProvenance(path, confidence, value, suggestion?.Invoke(value)));
        return value;
    }

    /// <summary>
    /// Proposes an international form for a phone number that was written nationally, or nothing.
    /// </summary>
    /// <remarks>
    /// Nothing is proposed without a country named in the candidate's own location: a prefix guessed
    /// from anywhere else would be a fact this code invented, and a plausible one accepted without
    /// reading is exactly the wrong data this product may not hold. See <see cref="PhoneCountryHints"/>.
    /// </remarks>
    private static string? PhoneSuggestion(string value, string? location)
    {
        // Already international. Nothing to correct, and re-prefixing would corrupt it.
        if (value.TrimStart().StartsWith('+'))
            return null;

        var code = PhoneCountryHints.DialingCodeFor(location);
        if (code is null)
            return null;

        var digits = new string(value.Where(char.IsDigit).ToArray());
        var proposed = $"+{code}{digits}";

        // Held to the same rule the Domain enforces, so a suggestion can never be a value that would be
        // rejected the moment the candidate accepted it — which would be a worse experience than the
        // refusal it replaces.
        return proposed.Length is >= 8 and <= 16 && digits.Length > 0 ? proposed : null;
    }

    /// <summary>
    /// Proposes the scheme a bare host is missing.
    /// </summary>
    /// <remarks>
    /// Safe in a way the phone hint is not: this invents no fact about the candidate, it writes out in
    /// full what they already wrote. `https` rather than `http` because a site that serves neither is
    /// not reachable anyway, and one that serves both should be linked over the secure one.
    /// </remarks>
    private static string? WebsiteSuggestion(string value)
    {
        var trimmed = value.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var asWritten)
            && asWritten.Scheme is "http" or "https")
        {
            return null;
        }

        var proposed = $"https://{trimmed}";
        return Uri.TryCreate(proposed, UriKind.Absolute, out var uri)
            && uri.Scheme == "https"
            // A host with no dot is not a site — "localhost", or a stray word the URL matcher caught.
            && uri.Host.Contains('.', StringComparison.Ordinal)
            ? proposed
            : null;
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

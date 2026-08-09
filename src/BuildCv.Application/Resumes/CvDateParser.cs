namespace BuildCv.Application.Resumes;

using System.Globalization;
using System.Text.RegularExpressions;
using BuildCv.Domain.Common.ValueObjects;

/// <summary>
/// One date read from CV text: either a date stated to some precision the domain can hold, or a date
/// that was recognised and still could not be resolved to one.
/// </summary>
/// <param name="Value">
/// The date, written to THE PRECISION THE SOURCE STATED and no finer: <c>yyyy-MM-dd</c> when the source
/// gave a day, <c>yyyy-MM</c> when it gave a month and a year, <c>yyyy</c> when it gave a bare year. Null
/// when the span was recognised as a date attempt but resolves to nothing — a two-digit year, an
/// impossible calendar date, a month outside 1..12. See <see cref="CvDateParser"/>.
/// </param>
/// <param name="Recognized">
/// True when the parser is willing to TREAT this span as a date — a full date, a month name + year, a
/// bare year, a numeric month/year, or a numeric date with a 2-digit year — even if <see cref="Value"/>
/// is null because it resolves to nothing. A recognised atom is what lets a line become a dated entry, so
/// the caller can flag "there was a date here I could not complete" rather than stay silent. False marks
/// a date-SHAPED span the parser will NOT stand behind as a date on its own — a
/// <c>"&lt;word&gt; &lt;year&gt;"</c> whose word is not a month ("Invierno 2020", "Engineer 2019").
/// Such a span never turns a line into a dated entry by itself, but it still HOLDS its position inside a
/// range, so the real date beside it is not misattributed to the wrong end (see <see cref="FindRange"/>).
/// </param>
/// <param name="SourceText">The exact snippet recognised, for the review screen to show back.</param>
public sealed record CvDate(string? Value, bool Recognized, string SourceText);

/// <summary>
/// A start–end date span read from one line, e.g. <c>"ene 2019 – dic 2021"</c> or <c>"2019 - Present"</c>.
/// </summary>
/// <param name="EndIsPresent">
/// True when the end was written as "Present" / "Actualidad" / "Actual". The end is then left blank on
/// purpose: inferring today's date from "Present" would invent a value the source does not contain.
/// </param>
public sealed record CvDateRange(CvDate Start, CvDate? End, bool EndIsPresent, string SourceText);

/// <summary>
/// Parses the date shapes real CVs use, each to THE PRECISION IT STATED and never one field finer.
/// </summary>
/// <remarks>
/// <para>
/// A full date is produced ONLY from a day+month+year source: <c>2019-03-15</c>, <c>15/03/2019</c>,
/// <c>15-03-2019</c>. A month-and-year source (<c>"Marzo 2020"</c>, <c>"01/2019"</c>, <c>"2019-03"</c>)
/// produces <c>yyyy-MM</c> and a bare year produces <c>yyyy</c> — NOT <c>2020-03-01</c>, which would
/// invent a day the candidate never wrote. The governing rule has not moved: nothing here fills in a
/// field the source did not state. What changed is what the field can hold — the domain's
/// <c>PartialDate</c> can now carry a date with no day, so the honest reading of "Marzo 2020" is a value
/// rather than a blank. Month/year is the dominant format on real CVs, which is why this is the
/// difference between a candidate confirming a date and re-typing one.
/// </para>
/// <para>
/// A span that resolves to NOTHING is still flagged rather than dropped: a two-digit year, a calendar
/// date that does not exist, a numeric month outside 1..12. Those return a null value with
/// <c>Recognized</c> true, so the entry still appears on the review screen with its source snippet.
/// </para>
/// <para>
/// Numeric dates are read DAY-FIRST (<c>15/03/2019</c> is 15 March), the Spanish-market convention; when
/// the first field exceeds 12 the reading is unambiguous anyway. This is a documented assumption, which is
/// why an extracted full date is only ever offered at medium confidence for the candidate to confirm.
/// </para>
/// </remarks>
public static class CvDateParser
{
    private static readonly IReadOnlyDictionary<string, int> Months = BuildMonths();

    private static readonly Regex PresentMarker = new(
        @"\b(present|presente|actualidad|actual|current|ongoing|hoy)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Ordered alternation: the full-date shapes come first so a full date is never mis-read as the bare
    // year inside it. A range separator is a dash WITH surrounding space; a date-internal separator has
    // none, which is what keeps "2019 - 2021" two atoms and "15-03-2019" one.
    private static readonly Regex Atom = new(
        @"(?<iso>\b\d{4}-\d{1,2}-\d{1,2}\b)"
        + @"|(?<dmy>\b\d{1,2}[/.]\d{1,2}[/.]\d{4}\b)"
        + @"|(?<dmydash>\b\d{1,2}-\d{1,2}-\d{4}\b)"
        + @"|(?<monthyear>\b(?<mw>\p{L}+)\.?\s+(?:del?\s+)?(?<my>\d{4})\b)"
        + @"|(?<nummonthyear>\b\d{1,2}/\d{4}\b)"
        + @"|(?<yearmonth>\b\d{4}[-/]\d{1,2}\b)"
        + @"|(?<shortdate>\b(?<sm>\d{1,2})[/.](?<sy>\d{2})\b)"
        + @"|(?<year>\b(?:19|20)\d{2}\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The first RECOGNISED date in <paramref name="text"/>, or null when there is none.</summary>
    public static CvDate? ParseSingle(string text) => ScanAtoms(text).FirstOrDefault(atom => atom.Recognized);

    /// <summary>The start–end span in <paramref name="line"/>, or null when the line has no date at all.</summary>
    public static CvDateRange? FindRange(string line)
    {
        var atoms = ScanAtoms(line).ToList();
        var presentMatch = PresentMarker.Match(line);
        var hasPresent = presentMatch.Success;

        // A line becomes a dated entry only if at least one span was RECOGNISED as a date. A placeholder
        // atom — a "<word> <year>" whose word is not a month — never anchors an entry on its own (else a
        // title line like "Software Engineer 2019" would sprout a phantom job), but when a recognised date
        // IS present the placeholder keeps its position, so the surviving date holds its true start/end
        // slot rather than being silently promoted to the wrong end.
        if (!atoms.Any(atom => atom.Recognized))
            return null;

        var start = atoms[0];
        if (atoms.Count >= 2)
            return new CvDateRange(start, atoms[1], EndIsPresent: false, line.Trim());

        // One date plus a "Present" marker that comes after it is the "2019 - Present" shape: an open end,
        // deliberately left blank rather than filled with today.
        if (hasPresent && presentMatch.Index > 0)
            return new CvDateRange(start, End: null, EndIsPresent: true, line.Trim());

        return new CvDateRange(start, End: null, EndIsPresent: false, line.Trim());
    }

    private static IEnumerable<CvDate> ScanAtoms(string text)
    {
        foreach (Match match in Atom.Matches(text))
        {
            var source = match.Value.Trim();

            if (match.Groups["iso"].Success)
            {
                if (TryFullDate(match.Value, "yyyy-M-d", out var iso))
                    yield return new CvDate(iso, Recognized: true, source);
                else
                    yield return new CvDate(null, Recognized: true, source);
            }
            else if (match.Groups["dmy"].Success)
            {
                yield return DayFirst(match.Value.Replace('.', '/'), source);
            }
            else if (match.Groups["dmydash"].Success)
            {
                yield return DayFirst(match.Value.Replace('-', '/'), source);
            }
            else if (match.Groups["monthyear"].Success)
            {
                // Only a real month word makes this a date; "de 2019" or "Ingeniero 2019" is not one. A
                // non-month word+year is still yielded, but as an UNRECOGNISED placeholder: the regex has
                // already consumed the span, so dropping it silently would let a following date slide into
                // the slot this one occupied — "Invierno 2020 - 15/03/2021" would promote 15/03/2021 to the
                // START and lose the real start. The placeholder does not turn a line into a dated entry on
                // its own (FindRange requires a RECOGNISED atom), but it holds this position inside a range.
                //
                // "Marzo 2020" resolves to 2020-03 — month precision, which is what it says. Whether the
                // word is a month decides RECOGNITION exactly as it did; only the value is new.
                yield return Months.TryGetValue(ResumeSectionHeadings.Normalize(match.Groups["mw"].Value), out var namedMonth)
                    ? new CvDate(YearMonth(match.Groups["my"].Value, namedMonth), Recognized: true, source)
                    : new CvDate(null, Recognized: false, source);
            }
            else if (match.Groups["nummonthyear"].Success)
            {
                // "06/2015". A four-digit second field is a year, so this is unambiguous even though the
                // day-first assumption governs the three-field numeric shapes above.
                var parts = match.Value.Split('/');
                yield return new CvDate(YearMonth(parts[1], parts[0]), Recognized: true, source);
            }
            else if (match.Groups["yearmonth"].Success)
            {
                // "2015-06" or "2015/6". A month outside 1..12 resolves to nothing and stays flagged —
                // "2015-13" is a date attempt, not a date.
                var parts = match.Value.Split('-', '/');
                yield return new CvDate(YearMonth(parts[0], parts[1]), Recognized: true, source);
            }
            else if (match.Groups["shortdate"].Success)
            {
                // A numeric date with a 2-DIGIT year (mm/yy), e.g. "03/95". Recognised as a date ATTEMPT so
                // a job dated only "03/95 - 06/98" still becomes an entry (its dates flagged NotExtracted)
                // instead of vanishing with no trace — but never resolved to a full yyyy-MM-dd, because a
                // 2-digit year is ambiguous. Guarded so a ratio like "8/10" or "15/20" is not read as a
                // date: the month must be 1..12 AND the year 13..99 (unambiguously a year, not a month or a
                // small denominator). 2-digit years 00..12 stay unrecognised on purpose — the ambiguity
                // there is not worth reading "9/10" as a date.
                if (int.TryParse(match.Groups["sm"].Value, out var sm) && sm is >= 1 and <= 12
                    && int.TryParse(match.Groups["sy"].Value, out var sy) && sy is >= 13 and <= 99)
                    yield return new CvDate(null, Recognized: true, source);
            }
            else
            {
                // A bare year. The regex admits only 19xx and 20xx, so this always resolves.
                yield return new CvDate(Year(match.Value), Recognized: true, source);
            }
        }
    }

    private static CvDate DayFirst(string slashed, string source)
    {
        var parts = slashed.Split('/');
        if (parts.Length == 3
            && int.TryParse(parts[0], out var day)
            && int.TryParse(parts[1], out var month)
            && int.TryParse(parts[2], out var year)
            && TryBuild(year, month, day, out var value))
        {
            return new CvDate(value, Recognized: true, source);
        }

        // Recognisably a date attempt, just not a valid calendar date — still flag it, do not fill it.
        return new CvDate(null, Recognized: true, source);
    }

    private static bool TryFullDate(string text, string format, out string value)
    {
        if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryBuild(int year, int month, int day, out string value)
    {
        if (year is >= 1900 and <= 2100
            && month is >= 1 and <= 12
            && day >= 1 && day <= DateTime.DaysInMonth(year, month))
        {
            value = new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        value = string.Empty;
        return false;
    }

    // The partial forms are written by PartialDate rather than by a format string here, so what this
    // parser emits is by construction what ResumeDraftValidator accepts and what the column stores.
    // Anything outside the calendar or outside the plausible-year window resolves to nothing; the caller
    // still shows the source snippet, which is the pre-existing "recognised but not resolved" path.
    private static string? YearMonth(string yearText, string monthText) =>
        int.TryParse(monthText, out var month) ? YearMonth(yearText, month) : null;

    private static string? YearMonth(string yearText, int month) =>
        int.TryParse(yearText, out var year) && IsPlausibleYear(year) && month is >= 1 and <= 12
            ? PartialDate.FromYearMonth(year, month).ToIsoString()
            : null;

    private static string? Year(string yearText) =>
        int.TryParse(yearText, out var year) && IsPlausibleYear(year)
            ? PartialDate.FromYear(year).ToIsoString()
            : null;

    // The same window TryBuild applies to a full date, so one parser does not read "0450" as a year in
    // one shape and refuse it in another.
    private static bool IsPlausibleYear(int year) => year is >= 1900 and <= 2100;

    private static Dictionary<string, int> BuildMonths()
    {
        var months = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(int number, params string[] names)
        {
            foreach (var name in names)
                months[ResumeSectionHeadings.Normalize(name)] = number;
        }

        Add(1, "January", "Jan", "Enero", "Ene");
        Add(2, "February", "Feb", "Febrero");
        Add(3, "March", "Mar", "Marzo");
        Add(4, "April", "Apr", "Abril", "Abr");
        Add(5, "May", "Mayo");
        Add(6, "June", "Jun", "Junio");
        Add(7, "July", "Jul", "Julio");
        Add(8, "August", "Aug", "Agosto", "Ago");
        Add(9, "September", "Sep", "Sept", "Septiembre", "Setiembre", "Set");
        Add(10, "October", "Oct", "Octubre");
        Add(11, "November", "Nov", "Noviembre");
        Add(12, "December", "Dec", "Diciembre", "Dic");

        return months;
    }
}

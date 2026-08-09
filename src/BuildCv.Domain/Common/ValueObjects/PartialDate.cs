using System.Globalization;
using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

/// <summary>
/// A calendar date stated to whatever precision its source actually stated: a full day, a year and a
/// month, or a bare year.
/// </summary>
/// <remarks>
/// <para>
/// THERE IS NO PRECISION FLAG, AND THAT IS THE WHOLE DESIGN. A date that does not have a day does not
/// HAVE one: <see cref="Day"/> is null, and <see cref="Month"/> is null when the source stated only a
/// year. The obvious alternative — a <see cref="DateOnly"/> carrying a sentinel day of the 1st with a
/// precision enum beside it — works only for as long as every reader remembers to consult the enum, and
/// the first reader that forgets reads <c>2015-06-01</c> as the first of June and is silently wrong.
/// Here that reader cannot exist: there is no member to read a day out of, and the compiler refuses to
/// use a <c>int?</c> as an <c>int</c>. The mistake is impossible rather than tested against.
/// </para>
/// <para>
/// THE ILLEGAL STATE HAS NO CONSTRUCTOR. A day without a month would be nonsense, so there is no
/// constructor that takes one: the three private constructors are year, year+month, and the one
/// <see cref="FromDate"/> uses, and each assigns exactly the fields its precision has. That is why
/// there is no defensive guard for the combination — a guard would be a branch nothing can reach.
/// </para>
/// <para>
/// A FULL-PRECISION VALUE IS ALWAYS BUILT FROM A REAL <see cref="DateOnly"/>, so "is the 29th a day of
/// this February" is <see cref="DateOnly"/>'s rule rather than a second copy of it here. That also
/// makes <see cref="FromDate"/> total: every <see cref="DateOnly"/> that exists converts, including the
/// extremes, which is what lets every date already persisted survive this type's arrival unchanged.
/// </para>
/// <para>
/// <see cref="EarliestDay"/> and <see cref="LatestDay"/> are the only views onto a real
/// <see cref="DateOnly"/>, and both are named for which end of the stated precision they are. At full
/// precision they are the same day, which is what makes every existing behaviour reproduce exactly.
/// The duration convention that uses them lives on <see cref="DateRange"/>.
/// </para>
/// </remarks>
public sealed record PartialDate
{
    /// <summary>The four-digit year. Always stated — a date with no year at all is not a date.</summary>
    public int Year { get; }

    /// <summary>The month, or null when the source stated only a year.</summary>
    public int? Month { get; }

    /// <summary>The day, or null when the source did not state one. Never set without <see cref="Month"/>.</summary>
    public int? Day { get; }

    private PartialDate(int year) => Year = year;

    private PartialDate(int year, int month)
    {
        Year = year;
        Month = month;
    }

    private PartialDate(int year, int month, int day)
    {
        Year = year;
        Month = month;
        Day = day;
    }

    /// <summary>The first day this value could mean: January 1st for a year, the 1st for a month.</summary>
    public DateOnly EarliestDay => new(Year, Month ?? 1, Day ?? 1);

    /// <summary>The last day this value could mean: December 31st for a year, the month's last day for a month.</summary>
    public DateOnly LatestDay => Day is { } day
        ? new DateOnly(Year, Month!.Value, day)
        : Month is { } month
            ? new DateOnly(Year, month, DateTime.DaysInMonth(Year, month))
            : new DateOnly(Year, 12, 31);

    /// <summary>Total: every <see cref="DateOnly"/> is a full-precision <see cref="PartialDate"/>.</summary>
    public static PartialDate FromDate(DateOnly date) => new(date.Year, date.Month, date.Day);

    public static PartialDate FromYearMonth(int year, int month)
    {
        RequireYear(year);
        if (month is < 1 or > 12)
            throw new InvalidPartialDateException("Month must be between 1 and 12.");

        return new PartialDate(year, month);
    }

    public static PartialDate FromYear(int year)
    {
        RequireYear(year);
        return new PartialDate(year);
    }

    /// <summary>
    /// The canonical text form, and the only one: <c>yyyy-MM-dd</c>, <c>yyyy-MM</c> or <c>yyyy</c>.
    /// </summary>
    /// <remarks>
    /// PRECISION IS THE LENGTH, which is what makes the form self-describing without a marker character
    /// and keeps every full date byte-identical to what this repository already wrote and already
    /// answers on the wire. It is also still ordered: comparing two of these as text orders them by
    /// <see cref="EarliestDay"/>, with a coarser value sorting before the finer ones inside it
    /// (<c>"2015" &lt; "2015-01" &lt; "2015-01-01" &lt; "2015-02"</c>), because each form is a prefix of
    /// the next and the fields are zero-padded.
    /// </remarks>
    public string ToIsoString() => Day is { } day
        ? string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month!.Value:D2}-{day:D2}")
        : Month is { } month
            ? string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{month:D2}")
            : Year.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>Reads <see cref="ToIsoString"/> back. Exact: nothing is trimmed and no other form is accepted.</summary>
    /// <remarks>
    /// ONE GRAMMAR, PARSED IN ONE PLACE. The persistence converter, the draft importer and the extractor
    /// all go through this, so "what a date looks like" cannot be stated twice and drift. It is
    /// deliberately strict about width — a two-digit year, an unpadded month or surrounding whitespace
    /// are all refused — because the length is what carries the precision, and a tolerant reader would
    /// make <c>"2015-6"</c> and <c>"2015-06"</c> two spellings of one value with only one of them ever
    /// written back.
    /// </remarks>
    public static bool TryParse(string? text, out PartialDate? date)
    {
        date = null;
        if (text is null)
            return false;

        var span = text.AsSpan();
        switch (span.Length)
        {
            case 4:
                if (!TryNumber(span, out var yearOnly) || !IsYear(yearOnly))
                    return false;
                date = new PartialDate(yearOnly);
                return true;

            case 7:
                if (span[4] != '-'
                    || !TryNumber(span[..4], out var yearOfMonth) || !IsYear(yearOfMonth)
                    || !TryNumber(span[5..], out var month) || month is < 1 or > 12)
                    return false;
                date = new PartialDate(yearOfMonth, month);
                return true;

            case 10:
                if (span[4] != '-' || span[7] != '-'
                    || !TryNumber(span[..4], out var year) || !IsYear(year)
                    || !TryNumber(span[5..7], out var fullMonth) || fullMonth is < 1 or > 12
                    || !TryNumber(span[8..], out var day)
                    || day < 1 || day > DateTime.DaysInMonth(year, fullMonth))
                    return false;
                date = new PartialDate(year, fullMonth, day);
                return true;

            default:
                return false;
        }
    }

    public override string ToString() => ToIsoString();

    // NumberStyles.None, so a sign, a thousands separator and any surrounding whitespace are all
    // refused. int.TryParse's default styles allow a leading sign and white space, which would let
    // "-015" and " 015" through as a year and give one date two spellings.
    private static bool TryNumber(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    // The range DateOnly itself accepts, not a narrower editorial one: FromDate has to be total over
    // every DateOnly that already exists in a persisted row.
    private static bool IsYear(int year) => year is >= 1 and <= 9999;

    private static void RequireYear(int year)
    {
        if (!IsYear(year))
            throw new InvalidPartialDateException("Year must be between 1 and 9999.");
    }
}

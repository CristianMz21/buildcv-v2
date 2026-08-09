using BuildCv.Domain.Common.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// DateRange is stored as "<start>/<end>", where each endpoint is written to the precision it actually
// has — "yyyy-MM-dd", "yyyy-MM" or "yyyy" — and an open-ended range keeps the separator with an empty
// end segment ("2020-01-01/"). The grammar for one endpoint is PartialDate.ToIsoString/TryParse and is
// not restated here, so the column and the wire cannot disagree about what a date looks like.
//
// EVERY ROW WRITTEN BEFORE PARTIAL PRECISION READS BACK UNCHANGED, and that is a property of the
// encoding rather than of a migration: full precision still writes exactly the ten characters it always
// wrote, so a stored "2020-01-15/2023-07-01" is still in the grammar and still parses to the same
// DateRange. That is why THERE IS NO MIGRATION — the column is varchar(21) and stays varchar(21),
// because the partial forms are SHORTER than the full one and the widest value this converter can
// produce is still two full dates and a separator. DateRangeConverter tests execute the old-row read
// against literal legacy text rather than round-tripping something this build wrote, which is the only
// version of that test that could fail.
//
// THE ROLLBACK DIRECTION IS THE ONE THAT BREAKS, and it does not need a schema change to break: a row
// written after this ships may hold "2015-06/2019-02", and a build from before it parses each segment
// with DateOnly.ParseExact("yyyy-MM-dd") and throws FormatException. Period is an eagerly-loaded owned
// property, so that is not a lost field — it is a resume that no longer loads. Announce it with the
// deploy: rolling back past this release means those rows must go first.
//
// It remains ordered, which is what this plaintext column exists for: comparing two stored values as
// text orders them by the first day the start could mean, a coarser value sorting immediately before the
// finer ones inside it, because each written form is a prefix of the next and every field is zero-padded.
//
// Reading goes back through DateRange.Create, so a persisted end-before-start fails loudly instead of
// materializing an aggregate the Domain would have rejected.
//
// The unreadable-endpoint messages quote the offending segment, which the previous implementation also
// did — DateOnly.ParseExact puts the string it refused in its own message. It is kept because a corrupt
// row cannot be diagnosed without it, and it is safe to keep for the reason this column is plaintext at
// all: a date range is analytical data, not the free text the encrypted columns hold.
internal sealed class DateRangeConverter() : ValueConverter<DateRange, string>(
    period => ToText(period),
    text => FromText(text))
{
    // "yyyy-MM-dd" + "/" + "yyyy-MM-dd", the widest a range can be written. Unchanged by partial
    // precision, which only ever writes less.
    public const int MaxLength = 21;

    private const char Separator = '/';

    public static string ToText(DateRange period)
    {
        ArgumentNullException.ThrowIfNull(period);

        var end = period.End is { } endDate ? endDate.ToIsoString() : string.Empty;

        return $"{period.Start.ToIsoString()}{Separator}{end}";
    }

    public static DateRange FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var separatorIndex = text.IndexOf(Separator);
        if (separatorIndex < 0)
            throw new FormatException(
                $"Persisted date range must be '<start>{Separator}<end>', each endpoint written as "
                + "yyyy-MM-dd, yyyy-MM or yyyy, with an optionally empty end segment.");

        if (!PartialDate.TryParse(text[..separatorIndex], out var start) || start is null)
            throw new FormatException($"Persisted date range has an unreadable start: '{text[..separatorIndex]}'.");

        var endText = text[(separatorIndex + 1)..];
        PartialDate? end = null;
        if (endText.Length > 0 && (!PartialDate.TryParse(endText, out end) || end is null))
            throw new FormatException($"Persisted date range has an unreadable end: '{endText}'.");

        return DateRange.Create(start, end);
    }
}

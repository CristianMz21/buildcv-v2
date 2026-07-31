using System.Globalization;
using BuildCv.Domain.Common.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// DateRange is stored as "yyyy-MM-dd/yyyy-MM-dd"; an open-ended range keeps the separator and leaves
// the end segment empty ("2020-01-01/"). Fixed width and lexicographically ordered by start date, so
// it stays useful for the plaintext analytics this column exists for.
//
// Reading goes back through DateRange.Create, so a persisted end-before-start fails loudly instead
// of materializing an aggregate the Domain would have rejected.
internal sealed class DateRangeConverter() : ValueConverter<DateRange, string>(
    period => ToText(period),
    text => FromText(text))
{
    // "yyyy-MM-dd" + "/" + "yyyy-MM-dd"
    public const int MaxLength = 21;

    private const string DateFormat = "yyyy-MM-dd";
    private const char Separator = '/';

    public static string ToText(DateRange period)
    {
        ArgumentNullException.ThrowIfNull(period);

        var end = period.End is { } endDate
            ? endDate.ToString(DateFormat, CultureInfo.InvariantCulture)
            : string.Empty;

        return $"{period.Start.ToString(DateFormat, CultureInfo.InvariantCulture)}{Separator}{end}";
    }

    public static DateRange FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var separatorIndex = text.IndexOf(Separator);
        if (separatorIndex < 0)
            throw new FormatException($"Persisted date range must be '{DateFormat}{Separator}{DateFormat}' with an optionally empty end segment.");

        var start = DateOnly.ParseExact(text[..separatorIndex], DateFormat, CultureInfo.InvariantCulture);
        var endText = text[(separatorIndex + 1)..];
        DateOnly? end = endText.Length == 0
            ? null
            : DateOnly.ParseExact(endText, DateFormat, CultureInfo.InvariantCulture);

        return DateRange.Create(start, end);
    }
}

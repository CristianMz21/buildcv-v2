namespace BuildCv.Application.Common;

using System.Globalization;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;

/// <summary>
/// The shared field-error validation mechanism for every bulk-import use case. It turns a domain
/// factory's throw into a <see cref="FieldError"/> keyed by JSON path, and it COLLECTS rather than
/// fails fast so a whole draft's problems come back in one pass.
/// </summary>
/// <remarks>
/// VALIDATION AND CONSTRUCTION ARE THE SAME PASS, ON PURPOSE. Nothing here re-implements a rule: every
/// verdict is produced by calling the real Domain factory — through <see cref="Build{T}"/>,
/// <see cref="BuildRequired{T}"/>, <see cref="Add"/> and the parse helpers — and catching what it threw.
/// A separate "check first, build later" validator would be two statements of the same rule, and the day
/// they disagree the API answers 200 to a request that then throws inside the handler, or 400 to one the
/// Domain would have accepted.
/// <para>
/// This class holds ONLY the domain-agnostic half of that pass. The per-section walkers — which draft
/// maps to which aggregate — live in each use case's own validator (<c>ResumeDraftValidator</c>,
/// <c>JobOfferDraftValidator</c>), which bring these helpers in with <c>using static</c> so their call
/// sites read the same as when the helpers were private. Extracting them here is what keeps the two
/// importers ONE mechanism rather than two copies of it.
/// </para>
/// </remarks>
public static class FieldErrorCollector
{
    private const string DateFormat = "yyyy-MM-dd";

    public const string RequiredMessage = "Value is required.";
    public const string ControlCharacterMessage = "Value must not contain control characters.";
    public const string InvalidDateMessage = "Invalid date. Expected yyyy-MM-dd.";
    public const string InvalidNumberMessage = "Invalid number.";

    // Walks one section, or refuses to. Over-cap returns WITHOUT iterating — building a hundred thousand
    // value objects is exactly the work the cap exists to decline — but only this section is skipped, so
    // the errors in the sections beside it are still collected.
    //
    // A NULL ELEMENT is a field error at its own index, not a crash. `[null]` and `[{}, null]` are valid
    // JSON and System.Text.Json does not enforce nullable reference annotations, so a null arrives here
    // as a real element whatever the declared type says. Handling it once, here, covers every section of
    // every draft and every nested list.
    public static void ForEachCapped<TDraft>(
        IReadOnlyList<TDraft?>? items, string path, int limit, List<FieldError> errors, Action<TDraft, string> handle)
        where TDraft : class
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(handle);

        if (items is null || items.Count == 0)
            return;

        if (items.Count > limit)
        {
            errors.Add(new FieldError(path, $"Too many items. At most {limit} are accepted."));
            return;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            var item = items[index];

            if (item is null)
                errors.Add(new FieldError(itemPath, RequiredMessage));
            else
                handle(item, itemPath);
        }
    }

    // The ONE place a Domain rule becomes a field error. Every factory in the Domain signals a violation
    // as a DomainException (invariants) or an ArgumentException (null, blank, out of range), and the
    // message it carries is the message the candidate reads.
    public static T? Build<T>(string path, List<FieldError> errors, Func<T> create) where T : class
    {
        try
        {
            return create();
        }
        catch (DomainException exception)
        {
            errors.Add(new FieldError(path, exception.Message));
            return null;
        }
        catch (ArgumentException exception)
        {
            errors.Add(new FieldError(path, ForACandidate(exception.Message)));
            return null;
        }
    }

    // Same two catches for the mutating half — an aggregate's Add* methods enforce duplicate and other
    // set-level rules that no constructor can see, because they are about the aggregate, not the item.
    public static void Add(string path, List<FieldError> errors, Action add)
    {
        try
        {
            add();
        }
        catch (DomainException exception)
        {
            errors.Add(new FieldError(path, exception.Message));
        }
        catch (ArgumentException exception)
        {
            errors.Add(new FieldError(path, ForACandidate(exception.Message)));
        }
    }

    // ArgumentException appends " (Parameter 'x')" to its message and ArgumentOutOfRangeException adds a
    // second line, "Actual value was N." Both are C# talking to a developer, and this text is rendered on
    // a review screen — the same reason Required states its own message for a blank rather than letting
    // the factory's parameter name through.
    //
    // The VERDICT is untouched; only the presentation is trimmed, and only for ArgumentException.
    // DomainException messages are written for a person and pass through unchanged.
    public static string ForACandidate(string message)
    {
        var firstLine = message.AsSpan();
        var lineBreak = firstLine.IndexOfAny('\r', '\n');
        if (lineBreak >= 0)
            firstLine = firstLine[..lineBreak];

        var parameterSuffix = firstLine.LastIndexOf(" (Parameter '");
        return parameterSuffix < 0 ? firstLine.ToString() : firstLine[..parameterSuffix].ToString();
    }

    // Blank is reported here rather than left to the factory only because the factory's own message for
    // it names a C# parameter ("Value cannot be null. (Parameter 'value')"), which is not something to
    // put on a review screen. The VERDICT is unchanged: every factory used through this rejects null and
    // blank through ArgumentException.ThrowIfNullOrWhiteSpace, so this cannot accept something they refuse.
    public static T? BuildRequired<T>(string path, string? value, Func<string, T> create, List<FieldError> errors)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new FieldError(path, RequiredMessage));
            return null;
        }

        return Build(path, errors, () => create(value));
    }

    // Blank is ABSENT, not invalid. Extraction routinely yields "" for a field it could not find, and the
    // per-section handlers already treat a missing optional as null.
    public static T? BuildOptional<T>(string path, string? value, Func<string, T> create, List<FieldError> errors)
        where T : class =>
        string.IsNullOrWhiteSpace(value) ? null : Build(path, errors, () => create(value));

    // Required plain text, for the identity fields a Domain record declares non-nullable while enforcing
    // nothing else about them. The non-null declaration IS the rule they have.
    //
    // CONTROL CHARACTERS ARE REFUSED, matching what every sibling value object already does:
    // PersonName.Create, OrganizationName.Create and Technology.Create all reject `.Any(char.IsControl)`,
    // and IsNullOrWhiteSpace does not — `"Music \r\nadmin: true"` is not whitespace. These are short
    // single-line values, several of which land in a plaintext column; use RequiredText instead for a
    // free-text field, where a newline is legitimate.
    public static string? Required(string path, string? value, List<FieldError> errors)
    {
        var text = RequiredText(path, value, errors);
        if (text is null)
            return null;

        if (!text.Any(char.IsControl))
            return text;

        errors.Add(new FieldError(path, ControlCharacterMessage));
        return null;
    }

    public static string? RequiredText(string path, string? value, List<FieldError> errors)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        errors.Add(new FieldError(path, RequiredMessage));
        return null;
    }

    public static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // yyyy-MM-dd exactly, because that is what the rest of the API already speaks: the per-section routes
    // bind DateOnly through System.Text.Json, whose converter accepts the ISO 8601 full-date form and
    // nothing else. TryParse with the ambient culture would make 03/04/2020 mean different days on
    // different servers.
    public static DateOnly? ParseDate(string path, string? value, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        errors.Add(new FieldError(path, InvalidDateMessage));
        return null;
    }

    public static int? ParseInt(string path, string? value, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        errors.Add(new FieldError(path, InvalidNumberMessage));
        return null;
    }

    public static DateRange? BuildRequiredPeriod(
        string startPath, string? start, string endPath, string? end, List<FieldError> errors)
    {
        var startDate = ParseDate(startPath, start, errors);
        var endDate = ParseDate(endPath, end, errors);

        if (startDate is null)
        {
            // A blank start is missing data. An unparseable one was already reported by ParseDate, and
            // reporting it twice would put two messages on one input.
            if (string.IsNullOrWhiteSpace(start))
                errors.Add(new FieldError(startPath, RequiredMessage));
            return null;
        }

        // An end that was sent and did not parse is already a recorded error, so nothing is silently
        // dropped by stopping here — but checking the range rule against a pair the candidate did not
        // send would answer a question they never asked.
        if (endDate is null && !string.IsNullOrWhiteSpace(end))
            return null;

        // "End date must be null or on/after start date." is DateRange.Create's own message, reported at
        // the END path because that is the input a review screen can usefully highlight.
        return Build(endPath, errors, () => DateRange.Create(startDate.Value, endDate));
    }

    public static DateRange? BuildOptionalPeriod(
        string startPath, string? start, string endPath, string? end, List<FieldError> errors)
    {
        var startDate = ParseDate(startPath, start, errors);
        var endDate = ParseDate(endPath, end, errors);

        if (startDate is null)
        {
            // A lone end date on an optional period is a value the candidate can see on their own review
            // screen, so it is refused rather than silently dropped the way the per-section handlers drop
            // it. A blank start with a blank end is simply absent.
            if (string.IsNullOrWhiteSpace(start) && !string.IsNullOrWhiteSpace(end))
                errors.Add(new FieldError(startPath, RequiredMessage));
            return null;
        }

        if (endDate is null && !string.IsNullOrWhiteSpace(end))
            return null;

        return Build(endPath, errors, () => DateRange.Create(startDate.Value, endDate));
    }

    public static TEnum? ParseRequiredEnum<TEnum>(
        string path, string? value, string invalidMessage, List<FieldError> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new FieldError(path, RequiredMessage));
            return null;
        }

        return ParseOptionalEnum<TEnum>(path, value, invalidMessage, errors);
    }

    // IsDefined is not belt-and-braces on top of TryParse. TryParse ACCEPTS any numeric string, and the
    // enums this fronts persist as tinyint through an unchecked conversion: "-1" wraps to 255 and "99"
    // stores as 99, silently. It lives HERE, once, rather than in each endpoint so there is one enum rule
    // per draft field, and so a bad level comes back as a field path beside the other failures instead of
    // as a bare 400.
    public static TEnum? ParseOptionalEnum<TEnum>(
        string path, string? value, string invalidMessage, List<FieldError> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;

        errors.Add(new FieldError(path, invalidMessage));
        return null;
    }
}

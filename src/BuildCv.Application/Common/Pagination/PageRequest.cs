namespace BuildCv.Application.Common.Pagination;

using BuildCv.Domain.Common.ValueObjects;

// What a caller may ask a list port for: how many rows, and where to resume.
//
// The two knobs fail differently on purpose. A limit is CLAMPED — asking for 0 or for 10,000 is a
// clumsy client, not a broken one, and the useful answer is a page rather than an error, while the
// ceiling is what stops "give me everything" from ever reaching the database again. A cursor is
// VALIDATED — it is a token this application minted, so a value that will not decode is either
// corrupted in transit or forged, and neither has a sensible page to fall back to. Falling back to the
// first page there would silently restart a client's walk from the top, which reads as data loss.
public sealed record PageRequest
{
    public const int DefaultLimit = 20;
    public const int MinLimit = 1;
    public const int MaxLimit = 100;

    public const string InvalidCursorError = "Invalid cursor.";

    private PageRequest(int limit, Cursor? cursor)
    {
        Limit = limit;
        Cursor = cursor;
    }

    public int Limit { get; }

    // Null means "first page". Non-null is already decoded, so no port below this type can be handed a
    // cursor it would have to validate again or, worse, ignore.
    public Cursor? Cursor { get; }

    public static Result<PageRequest> Create(int? limit, string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return Result<PageRequest>.Success(new PageRequest(ClampLimit(limit), null));

        return Pagination.Cursor.TryParse(cursor, out var parsed)
            ? Result<PageRequest>.Success(new PageRequest(ClampLimit(limit), parsed))
            : Result<PageRequest>.Failure(InvalidCursorError);
    }

    private static int ClampLimit(int? limit) =>
        limit is null ? DefaultLimit : Math.Clamp(limit.Value, MinLimit, MaxLimit);
}

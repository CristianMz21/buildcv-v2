namespace BuildCv.Application.Common.Pagination;

// The in-memory half of the keyset contract.
//
// The SQL translation belongs to Infrastructure, but the SEMANTICS cannot live there alone: the
// development store and the Application test fakes have to answer these ports the way SQL Server does —
// same order, same cursor boundary, same limit+1 truncation — or an Api test pinned to the in-memory
// provider certifies page behavior production has never produced. One implementation, called by every
// non-SQL store, is the cheapest way to keep them from drifting.
//
// The boundary is STRICT on both sides: a cursor names a row the caller already has, so including it
// again would duplicate that row across the page break.
public static class KeysetSequence
{
    public static Page<T> ToNewestFirstPage<T>(this IEnumerable<KeysetRow<T>> rows, PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(request);

        return Page<T>.From(
            [.. rows
                .Where(row => request.Cursor is null || row.Position < request.Cursor.Position)
                .OrderByDescending(row => row.Position)
                .Take(request.Limit + 1)],
            request);
    }

    public static Page<T> ToOldestFirstPage<T>(this IEnumerable<KeysetRow<T>> rows, PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(request);

        return Page<T>.From(
            [.. rows
                .Where(row => request.Cursor is null || row.Position > request.Cursor.Position)
                .OrderBy(row => row.Position)
                .Take(request.Limit + 1)],
            request);
    }
}

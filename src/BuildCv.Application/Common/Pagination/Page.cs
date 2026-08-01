namespace BuildCv.Application.Common.Pagination;

// One slice of a keyset-paginated list. NextCursor is null exactly when there is nothing after this
// page, which is the only stop signal a client gets — there is no total count on purpose, because
// counting is the linear-cost work keyset pagination exists to avoid.
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    // The limit+1 probe turned into a page. THE off-by-one lives here and only here, shared by the EF,
    // the in-memory and the test stores alike: three copies of this arithmetic would be three chances
    // to ship a phantom next page or a silently dropped last row, and both failures look like success.
    //
    // `probed` is the answer to asking for Limit + 1 rows in cursor order. An extra row means there is
    // more to read, so the probe row is dropped and the caller is handed a cursor pointing at the LAST
    // ROW IT ACTUALLY RECEIVED. Pointing it at the probe row instead would skip that row forever, since
    // the next query asks for rows strictly beyond the cursor.
    public static Page<T> From(IReadOnlyList<KeysetRow<T>> probed, PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(probed);
        ArgumentNullException.ThrowIfNull(request);

        var hasMore = probed.Count > request.Limit;
        var kept = hasMore ? request.Limit : probed.Count;
        var items = probed.Take(kept).Select(row => row.Item).ToList();

        return new Page<T>(items, hasMore ? Cursor.At(probed[kept - 1].Position).Encode() : null);
    }
}

// An entity paired with the keyset position it was read at.
//
// The pairing has to survive the query because the position does not survive the entity: Seq is a
// shadow column, so once EF has materialized a Resume there is no way left to ask where it sat in the
// index — and that number is exactly the next cursor.
public sealed record KeysetRow<T>(T Item, long Position);

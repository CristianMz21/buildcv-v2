namespace BuildCv.Api.Contracts;

/// <summary>
/// Pairs the entries of one aggregate's collections with the ids that address them, shared by the two
/// CV-shaped aggregates so their responses cannot drift apart.
/// </summary>
/// <remarks>
/// THROWS RATHER THAN ZIPPING when the counts disagree, and the difference is the whole point.
/// Enumerable.Zip stops at the shorter side, so an adapter that returned nine ids for ten skills would
/// silently drop the tenth from the response — a candidate's skill vanishing from their own data with no
/// error anywhere. A mismatch is a bug in a repository, cannot be caused by any request, and is not
/// something a client should be handed a half-answer for.
/// </remarks>
internal static class ItemIdProjection
{
    public static IReadOnlyList<TResponse> Project<TItem, TResponse>(
        string aggregate, IReadOnlyList<TItem> items, IReadOnlyList<int> ids, Func<int, TItem, TResponse> project)
    {
        if (items.Count != ids.Count)
        {
            throw new InvalidOperationException(
                $"{aggregate} item ids are misaligned: {ids.Count} ids for {items.Count} entries.");
        }

        var projected = new TResponse[items.Count];
        for (var position = 0; position < items.Count; position++)
            projected[position] = project(ids[position], items[position]);

        return projected;
    }
}

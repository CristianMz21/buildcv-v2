namespace BuildCv.Domain.Readability;

// THE order readability recommendations are read in, written once because two layers apply it.
//
// The Application layer sorts before persisting, so the ten that survive the cap are the ten that
// deserved to. The Api layer sorts again on the way out, because the child table carries a surrogate key
// and no stored position: a reloaded report hands its recommendations back in whatever order the server
// chose, and that order is not stable between reads. Two independent copies of these four comparisons
// would be two chances to invert one, and a persisted set that disagreed with the response would make
// every round-trip assertion flaky rather than failing.
//
// It is a TOTAL order on purpose. A partial one leaves ties resolved by whatever the sort happened to
// do, which is exactly the nondeterminism the cap makes visible -- two recommendations tied on priority
// and impact would take turns being the tenth.
public static class ReadabilityRecommendationOrder
{
    public static IComparer<ReadabilityRecommendation> Display { get; } =
        Comparer<ReadabilityRecommendation>.Create(Compare);

    public static IReadOnlyList<ReadabilityRecommendation> Sort(
        IEnumerable<ReadabilityRecommendation> recommendations)
    {
        ArgumentNullException.ThrowIfNull(recommendations);
        return [.. recommendations.Order(Display)];
    }

    // The four keys, and the two directions differ:
    //
    // 1. Priority ASCENDING. Critical is 0, so ascending puts the urgent advice first.
    // 2. Impact DESCENDING. Impact is how much score acting on this recovers, so the biggest win comes
    //    first WITHIN a priority.
    // 3. Section ascending, which is the numeric ReadabilitySectionType order and therefore the order
    //    the sections are read in everywhere else.
    // 4. Message, ordinal. The last resort, and it exists to make the order total rather than because
    //    alphabetical advice means anything. Ordinal, not culture-aware: a sort that changed with the
    //    server's locale would make the persisted set and the response disagree across machines.
    private static int Compare(ReadabilityRecommendation? left, ReadabilityRecommendation? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var byPriority = left.Priority.CompareTo(right.Priority);
        if (byPriority != 0)
            return byPriority;

        var byImpact = right.Impact.CompareTo(left.Impact);
        if (byImpact != 0)
            return byImpact;

        var bySection = left.Section.CompareTo(right.Section);
        return bySection != 0 ? bySection : string.CompareOrdinal(left.Message, right.Message);
    }
}

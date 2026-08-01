namespace BuildCv.Application.Tests.Common.Pagination;

using BuildCv.Application.Common.Pagination;
using FluentAssertions;

// The page boundary, tested where it actually lives.
//
// Page<T>.From is the single copy of the limit+1 arithmetic that the EF, in-memory and fake stores all
// call, so a bug here is a bug in every store at once — which is exactly why it is worth pinning here,
// in milliseconds, rather than only through a container.
public sealed class KeysetPagingTests
{
    [Fact]
    public void From_WithNothingToShow_IsAnEmptyFinalPage()
    {
        var page = Page<string>.From([], PageRequests.Of(10));

        page.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public void From_WhenTheProbeFoundFewerRowsThanTheLimit_IsTheLastPage()
    {
        var page = Page<string>.From([Row("a", 3), Row("b", 2)], PageRequests.Of(10));

        page.Items.Should().Equal("a", "b");
        page.NextCursor.Should().BeNull();
    }

    // The case an off-by-one loves: the last page is exactly full. The probe came back with LIMIT rows,
    // not limit+1, so there is nothing after it — handing out a cursor here would send the client back
    // for a page that is always empty.
    [Fact]
    public void From_WhenTheProbeFoundExactlyTheLimit_IsStillTheLastPage()
    {
        var page = Page<string>.From([Row("a", 3), Row("b", 2)], PageRequests.Of(2));

        page.Items.Should().Equal("a", "b");
        page.NextCursor.Should().BeNull();
    }

    // The other half of the same off-by-one. The probe row proves there is more, so it is dropped and
    // the cursor points at "b" — the last row the caller was actually given. Pointing it at "c" instead
    // compiles, reads plausibly, and loses "c" forever, because the next query asks for rows strictly
    // past the cursor.
    [Fact]
    public void From_WhenTheProbeFoundOneMore_DropsItAndPointsAtTheLastRowReturned()
    {
        var page = Page<string>.From([Row("a", 3), Row("b", 2), Row("c", 1)], PageRequests.Of(2));

        page.Items.Should().Equal("a", "b");
        page.NextCursor.Should().Be(Cursor.At(2).Encode());
    }

    // Seven rows at three per page: 3, 3, 1. Walked the way a client walks it, asserting the property
    // that matters more than any single page — every row exactly once, in order, and a stop signal that
    // arrives neither early nor late.
    [Fact]
    public void ToNewestFirstPage_WalkedByCursor_VisitsEveryRowOnceNewestFirst()
    {
        var rows = Enumerable.Range(1, 7).Select(n => Row($"row-{n}", n)).ToList();

        var pages = WalkNewestFirst(rows, limit: 3);

        pages.Select(page => page.Items.Count).Should().Equal(3, 3, 1);
        pages.SelectMany(page => page.Items).Should()
            .Equal("row-7", "row-6", "row-5", "row-4", "row-3", "row-2", "row-1");
        pages[^1].NextCursor.Should().BeNull();
    }

    // Eight rows at four per page is the boundary the walk above cannot reach: the second page ends
    // exactly on the last row, so the probe finds nothing and the walk has to stop after two pages
    // rather than fetching a third, empty one.
    [Fact]
    public void ToNewestFirstPage_WhenTheLastPageIsExactlyFull_StopsWithoutAnEmptyPage()
    {
        var rows = Enumerable.Range(1, 8).Select(n => Row($"row-{n}", n)).ToList();

        var pages = WalkNewestFirst(rows, limit: 4);

        pages.Should().HaveCount(2);
        pages.Select(page => page.Items.Count).Should().Equal(4, 4);
        pages[^1].NextCursor.Should().BeNull();
    }

    // The score-history direction. Same arithmetic, mirrored boundary: a cursor is still exclusive, so
    // walking forwards must not repeat the row the previous page ended on.
    [Fact]
    public void ToOldestFirstPage_WalkedByCursor_VisitsEveryRowOnceOldestFirst()
    {
        var rows = Enumerable.Range(1, 5).Select(n => Row($"row-{n}", n)).ToList();

        var visited = new List<string>();
        string? cursor = null;
        do
        {
            var page = rows.ToOldestFirstPage(PageRequests.Of(2, cursor));
            visited.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        visited.Should().Equal("row-1", "row-2", "row-3", "row-4", "row-5");
    }

    // The cursor names a row the caller already holds, so it must be excluded rather than repeated.
    [Fact]
    public void ToNewestFirstPage_ExcludesTheRowTheCursorPointsAt()
    {
        var rows = new[] { Row("a", 3), Row("b", 2), Row("c", 1) };

        var page = rows.ToNewestFirstPage(PageRequests.Of(10, Cursor.At(2).Encode()));

        page.Items.Should().Equal("c");
    }

    // A store that reports a non-positive position is broken, and the exception TYPE is the assertion.
    // ArgumentOutOfRangeException is an ArgumentException, which the handlers catch and convert into a
    // Result failure — so the sloppy version of this answers the client 400 with a message naming an
    // internal parameter. A server fault has to stay a server fault.
    [Fact]
    public void From_WhenTheStoreReportsAPositionNoSequenceCanProduce_IsAServerFaultNotAClientOne()
    {
        var act = () => Page<string>.From([Row("a", 0), Row("b", 0)], PageRequests.Of(1));

        // InvalidOperationException is not an ArgumentException, so this one assertion IS the "not a
        // 400" claim — spelling that out as a second NotBeOfType clause would read like a further guard
        // while being incapable of failing. What makes this discriminate is the revert: letting
        // Cursor.At throw again turns it red.
        act.Should().Throw<InvalidOperationException>();
    }

    // Insertion order is not iteration order in a dictionary-backed store, so the ordering has to be
    // done by the paging code rather than inherited from the source.
    [Fact]
    public void ToNewestFirstPage_OrdersByPositionRatherThanBySourceOrder()
    {
        var rows = new[] { Row("b", 2), Row("d", 4), Row("a", 1), Row("c", 3) };

        var page = rows.ToNewestFirstPage(PageRequests.Of(10));

        page.Items.Should().Equal("d", "c", "b", "a");
    }

    private static List<Page<string>> WalkNewestFirst(IEnumerable<KeysetRow<string>> rows, int limit)
    {
        var materialized = rows.ToList();
        var pages = new List<Page<string>>();
        string? cursor = null;

        do
        {
            var page = materialized.ToNewestFirstPage(PageRequests.Of(limit, cursor));
            pages.Add(page);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return pages;
    }

    private static KeysetRow<string> Row(string item, long position) => new(item, position);
}

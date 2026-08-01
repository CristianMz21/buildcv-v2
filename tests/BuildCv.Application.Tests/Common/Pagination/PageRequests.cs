namespace BuildCv.Application.Tests.Common.Pagination;

using BuildCv.Application.Common.Pagination;
using FluentAssertions;

// PageRequest.Create is the only way in and it returns a Result, because a cursor can be malformed.
// Tests that are not ABOUT malformed cursors say so once here rather than trailing `.Value!` through
// every call — and asserting success inside the helper turns a page request that failed to build into a
// failure on the line that built it, instead of a NullReferenceException three asserts later.
internal static class PageRequests
{
    public static PageRequest Of(int? limit = null, string? cursor = null)
    {
        var request = PageRequest.Create(limit, cursor);
        request.IsSuccess.Should().BeTrue("'{0}' was expected to be a usable cursor", cursor);
        return request.Value!;
    }
}

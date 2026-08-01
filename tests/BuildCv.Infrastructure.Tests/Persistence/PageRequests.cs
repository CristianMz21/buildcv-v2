using BuildCv.Application.Common.Pagination;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence;

// PageRequest.Create returns a Result because a cursor can be malformed. Tests that are not ABOUT
// malformed cursors say so once here, and asserting success inside the helper turns a page request that
// failed to build into a failure on the line that built it rather than a null three asserts later.
internal static class PageRequests
{
    public static PageRequest Of(int? limit = null, string? cursor = null)
    {
        var request = PageRequest.Create(limit, cursor);
        request.IsSuccess.Should().BeTrue("'{0}' was expected to be a usable cursor", cursor);
        return request.Value!;
    }
}

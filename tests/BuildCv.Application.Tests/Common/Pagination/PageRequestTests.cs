namespace BuildCv.Application.Tests.Common.Pagination;

using BuildCv.Application.Common.Pagination;
using FluentAssertions;

public sealed class PageRequestTests
{
    [Fact]
    public void Create_WithNoLimit_UsesTheDefault()
    {
        PageRequests.Of().Limit.Should().Be(PageRequest.DefaultLimit);
    }

    // Clamped, not rejected. A limit is a hint about page size, and the useful answer to a clumsy one is
    // a page — the ceiling is there to stop "give me everything" from reaching the database, not to
    // punish a client for asking.
    [Theory]
    [InlineData(0, PageRequest.MinLimit)]
    [InlineData(-5, PageRequest.MinLimit)]
    [InlineData(int.MinValue, PageRequest.MinLimit)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(100, PageRequest.MaxLimit)]
    [InlineData(1000, PageRequest.MaxLimit)]
    [InlineData(int.MaxValue, PageRequest.MaxLimit)]
    public void Create_ClampsTheLimitIntoTheSupportedRange(int requested, int expected)
    {
        PageRequests.Of(requested).Limit.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Create_WithNoCursor_IsTheFirstPage(string? cursor)
    {
        PageRequests.Of(10, cursor).Cursor.Should().BeNull();
    }

    [Fact]
    public void Create_WithACursorThisApplicationMinted_CarriesItDecoded()
    {
        var request = PageRequests.Of(10, Cursor.At(77).Encode());

        request.Cursor.Should().NotBeNull();
        request.Cursor!.Position.Should().Be(77);
    }

    // A cursor is a token the server minted, so one that will not decode is corrupt or forged and there
    // is no honest page to fall back to. Silently starting over would restart a client's walk from the
    // top and look exactly like the data underneath it had changed.
    [Theory]
    [InlineData("nonsense")]
    [InlineData("AAAAAAAAAAA")]
    [InlineData("' OR 1=1 --")]
    public void Create_WithACursorItCannotDecode_FailsRatherThanStartingOver(string cursor)
    {
        var result = PageRequest.Create(20, cursor);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PageRequest.InvalidCursorError);
    }

    // The error text is load-bearing: ResultExtensions.ToHttpResult routes on it, and anything ending in
    // "not found." or equal to "Forbidden." would leave a malformed cursor answering 404 or 403.
    [Fact]
    public void InvalidCursorError_MapsToABadRequestUnderTheApiConvention()
    {
        PageRequest.InvalidCursorError.Should().NotBe("Forbidden.");
        PageRequest.InvalidCursorError.Should().NotEndWith("not found.");
    }
}

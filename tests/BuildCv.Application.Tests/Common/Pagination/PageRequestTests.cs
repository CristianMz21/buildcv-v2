namespace BuildCv.Application.Tests.Common.Pagination;

using BuildCv.Application.Common.Pagination;
using FluentAssertions;

public sealed class PageRequestTests
{
    // EVERY EXPECTATION IN THIS FILE IS A LITERAL, and that is the whole point of it — see the note
    // above Create_ClampsTheLimitIntoTheSupportedRange. Written as Be(PageRequest.DefaultLimit) this
    // line held for every value the constant could take.
    [Fact]
    public void Create_WithNoLimit_UsesTwenty()
    {
        PageRequests.Of().Limit.Should().Be(20);
    }

    // Clamped, not rejected. A limit is a hint about page size, and the useful answer to a clumsy one is
    // a page — the ceiling is there to stop "give me everything" from reaching the database, not to
    // punish a client for asking.
    //
    // THE EXPECTATIONS ARE LITERALS, NOT THE CONSTANTS THE CODE USES. This test interpolated
    // PageRequest.MinLimit and PageRequest.MaxLimit on the right-hand side, so most of its rows compared
    // the constant against itself and held for every value of it. Measured at the time (issue #19): a
    // ceiling raised above 100 or a floor lowered below 20 was caught by the two rows that happened to
    // carry a literal, and a ceiling moved anywhere INSIDE [20, 100] — 100 to 50, halving a public page
    // size — landed green. It is failure mode 4 in this repository's catalogue: the test could not
    // observe the guarantee it was named after.
    //
    // Each bound is now closed from both sides, so the clamp cannot move in either direction silently:
    // (1, 1) reds if the floor rises, (0, 1) reds if it falls, (100, 100) and (1000, 100) red if the
    // ceiling moves either way.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(20, 20)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(1000, 100)]
    [InlineData(int.MaxValue, 100)]
    public void Create_ClampsTheLimitIntoTheSupportedRange(int requested, int expected)
    {
        PageRequests.Of(requested).Limit.Should().Be(expected);
    }

    // The three constants, named. This is a change detector on purpose: they are the public page
    // contract — MaxLimit is the ceiling that stops "give me everything" from reaching the database,
    // which is why no repository port has an unbounded list method — and moving one is a decision to
    // take deliberately, in a diff that says so, rather than something that lands inside another change.
    //
    // It is not a substitute for the rows above and does not make them redundant: this asserts the
    // constants, they assert that ClampLimit actually uses them.
    [Fact]
    public void TheLimitContractIsOneToOneHundred_DefaultingToTwenty()
    {
        PageRequest.MinLimit.Should().Be(1);
        PageRequest.MaxLimit.Should().Be(100);
        PageRequest.DefaultLimit.Should().Be(20);
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

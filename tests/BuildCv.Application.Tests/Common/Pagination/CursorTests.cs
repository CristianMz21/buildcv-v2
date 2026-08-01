namespace BuildCv.Application.Tests.Common.Pagination;

using BuildCv.Application.Common.Pagination;
using FluentAssertions;

public sealed class CursorTests
{
    [Theory]
    [InlineData(1L)]
    [InlineData(42L)]
    [InlineData(int.MaxValue + 1L)]
    [InlineData(long.MaxValue)]
    public void Encode_ThenTryParse_RoundTripsThePosition(long position)
    {
        var encoded = Cursor.At(position).Encode();

        Cursor.TryParse(encoded, out var parsed).Should().BeTrue();
        parsed!.Position.Should().Be(position);
    }

    // A cursor travels in a URL and, on this API, in a query string that also has to survive being
    // logged and copy-pasted. Anything that needs percent-encoding would come back mangled.
    [Fact]
    public void Encode_ProducesOnlyUrlSafeCharacters()
    {
        var encoded = Cursor.At(long.MaxValue).Encode();

        encoded.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    // Every one of these has to come back as a plain "no", because the alternative is worse than an
    // error: a cursor that silently decodes to 0 or to garbage means the caller is handed the first
    // page again, or a page from the middle of somebody else's walk, and told nothing went wrong.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("!!!!!!!!!!!")]
    [InlineData("AAAA")]                              // decodes, but to three bytes, not eight
    [InlineData("AAAAAAAAAAAAAAAA")]                  // decodes, but to twelve
    [InlineData("AAAAAAAAAAA")]                       // eight zero bytes: position 0, which is no row
    [InlineData("//////////8")]                       // base64, not base64url
    [InlineData("AAAAAAAAAA")]                        // ten characters: seven bytes, one short
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("42")]                                // the decimal spelling somebody will try by hand
    [InlineData("1 OR 1=1")]
    [InlineData("../../etc/passwd")]
    public void TryParse_ForAValueThisApplicationDidNotMint_Fails(string? value)
    {
        Cursor.TryParse(value, out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    // A negative long is eight perfectly well-formed bytes, so length alone does not catch it — and it
    // would read as "everything before row minus-something", which is the whole table on a descending
    // walk.
    [Fact]
    public void TryParse_ForAWellFormedEncodingOfANegativePosition_Fails()
    {
        var forged = Convert.ToBase64String(BitConverter.GetBytes(-1L))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        Cursor.TryParse(forged, out _).Should().BeFalse();
    }

    // Not client input: a store handing out a position of zero would be a bug in the store, and the
    // loud failure belongs there rather than in a cursor a caller can never use.
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void At_WithAPositionNoRowCanHave_Throws(long position)
    {
        var act = () => Cursor.At(position);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Cursors_AtTheSamePosition_AreEqual()
    {
        Cursor.At(7).Should().Be(Cursor.At(7));
        Cursor.At(7).Encode().Should().Be(Cursor.At(7).Encode());
    }
}

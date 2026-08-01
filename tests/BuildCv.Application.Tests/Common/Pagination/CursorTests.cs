namespace BuildCv.Application.Tests.Common.Pagination;

using System.Buffers.Binary;
using System.Buffers.Text;
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

    // A negative long is eleven perfectly well-formed characters, so neither the length gate nor the
    // alphabet catches it — and it would read as "everything before row minus-something", which is the
    // whole table on a descending walk. Only the `position <= 0` check stands between the two.
    //
    // Forged BIG-endian, matching the decoder. Doing it with BitConverter (little-endian on every
    // platform this runs on) would have made the test pass for the wrong reason with -1, whose bytes are
    // symmetric, and fail outright with long.MinValue, whose little-endian bytes read big-endian as a
    // perfectly acceptable positive 128.
    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void TryParse_ForAWellFormedEncodingOfANegativePosition_Fails(long position)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, position);
        var forged = Base64Url.EncodeToString(bytes);

        forged.Length.Should().Be(11, "the forgery has to be well formed for the rejection to mean anything");
        Cursor.TryParse(forged, out _).Should().BeFalse();
    }

    // The byte order, pinned against a value written out by hand rather than round-tripped through the
    // same code that produced it. Little-endian would encode position 1 as "AQAAAAAAAAA"; a round-trip
    // test cannot tell the two apart, because it would be wrong in both directions at once.
    [Fact]
    public void Encode_LaysThePositionOutBigEndian()
    {
        Cursor.At(1).Encode().Should().Be("AAAAAAAAAAE");
        Cursor.At(42).Encode().Should().Be("AAAAAAAAACo");
    }

    [Fact]
    public void Encode_IsAlwaysElevenCharacters()
    {
        foreach (var position in new[] { 1L, 42L, int.MaxValue + 1L, long.MaxValue })
            Cursor.At(position).Encode().Length.Should().Be(11);
    }

    // Base64Url.IsValid is more permissive than it looks: on its own it accepts embedded whitespace and
    // optional padding, so every one of these decodes to position 42. They are the same row, so nothing
    // is unsafe — but an opaque token with several spellings is a trap for the first thing downstream
    // that compares two of them, and the length gate is what makes the spelling unique.
    [Theory]
    [InlineData(" AAAAAAAAACo")]
    [InlineData("AAAAAAAAACo ")]
    [InlineData("AAAA AAAAACo")]
    [InlineData("AAAA\r\nAAAAACo")]
    [InlineData("AAAAAAAAACo=")]
    public void TryParse_ForANonCanonicalSpellingOfAValidPosition_Fails(string alias)
    {
        Cursor.TryParse(alias, out _).Should()
            .BeFalse("'{0}' is not the one spelling this application mints", alias);
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

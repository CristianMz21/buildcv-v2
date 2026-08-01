namespace BuildCv.Application.Common.Pagination;

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;

// The opaque token a client carries from one page to the next. It wraps exactly one number: the keyset
// position of the last row that caller was already handed, which is all keyset pagination needs to
// resume.
//
// Encoded as the eight big-endian bytes of that number in base64url, so every well-formed cursor is
// eleven URL-safe characters and everything else is rejected on length alone. A decimal string would
// have read better and validated worse: "12", " 12", "+12", "1e3", "-1" and a thirty-digit overflow all
// have to be ruled out by hand, and each one missed becomes a silent 0 — which means "start from the
// beginning" and quietly serves page one again instead of failing.
//
// Opaque, not secret. Base64url only stops clients from hand-writing positions and building a
// dependency on a number the server owns; anybody can decode it. Nothing here is a security boundary,
// and the position must never be used as an authorization input.
public sealed record Cursor
{
    private const int PositionByteCount = sizeof(long);

    private Cursor(long position) => Position = position;

    // The Seq of the last row already delivered. Strictly positive: Seq is a bigint IDENTITY seeded at
    // 1, so there is no row zero, and refusing zero keeps "no cursor" from ever being spelled two ways.
    public long Position { get; }

    public static Cursor At(long position)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(position);
        return new Cursor(position);
    }

    // Defensive by contract: this is fed straight from a query string, so a hostile or truncated value
    // is an ordinary input, not an exceptional one. It answers false and the caller turns that into a
    // Result failure — it never throws, and it never degrades into an unfiltered scan.
    public static bool TryParse(string? value, [NotNullWhen(true)] out Cursor? cursor)
    {
        cursor = null;

        if (string.IsNullOrEmpty(value))
            return false;

        // IsValid FIRST, and it is not redundant. Base64Url.TryDecodeFromChars answers false only for a
        // destination that is too small; for a character outside the alphabet it THROWS FormatException,
        // Try in the name notwithstanding. Reaching it with anything but a validated string would turn
        // "?cursor=nonsense" into a 500.
        if (!Base64Url.IsValid(value))
            return false;

        Span<byte> decoded = stackalloc byte[PositionByteCount];
        if (!Base64Url.TryDecodeFromChars(value, decoded, out var written) || written != PositionByteCount)
            return false;

        var position = BinaryPrimitives.ReadInt64BigEndian(decoded);
        if (position <= 0)
            return false;

        cursor = new Cursor(position);
        return true;
    }

    public string Encode()
    {
        Span<byte> buffer = stackalloc byte[PositionByteCount];
        BinaryPrimitives.WriteInt64BigEndian(buffer, Position);
        return Base64Url.EncodeToString(buffer);
    }
}

using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Security.Encryption;

namespace BuildCv.Infrastructure.Persistence.BlindIndexes;

// The only supported way to compute the RefreshTokens.TokenHash digest.
//
// Unlike Email there is no normalization step to get wrong: a refresh token is opaque base64url that
// the Domain stores verbatim, so the presented string IS the canonical form. The typed helper exists
// anyway, for the other half of the problem — the context string. "RefreshToken.Token" has to be
// spelled identically by the writer, by every reader, and by the column mapping; three literals in
// three files is three chances to typo one into a digest that matches nothing.
public sealed class RefreshTokenIndex
{
    // Must match the AAD context on the encrypted RefreshTokens.Token column.
    public const string Context = "RefreshToken.Token";

    private readonly IBlindIndex _blindIndex;

    public RefreshTokenIndex(IBlindIndex blindIndex)
    {
        ArgumentNullException.ThrowIfNull(blindIndex);
        _blindIndex = blindIndex;
    }

    // The digest to WRITE. Takes the entity rather than its string so a write cannot be computed for
    // a token that never passed the Domain's length and lifetime validation.
    //
    // WRITES ONLY, and there is exactly one caller: BlindIndexSaveChangesInterceptor. A read goes through
    // Persistence/EfCore/BlindIndexLookup, which takes the ComputeCandidates list and has no overload
    // that would accept this.
    public byte[] Compute(RefreshToken refreshToken)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        return _blindIndex.Compute(refreshToken.Token, Context);
    }

    // The digests to MATCH for a token presented by a client. This one has to accept a raw string:
    // at lookup time there is no entity yet, only whatever arrived in the cookie. That is safe here
    // precisely because the Domain applies no normalization to the token — the same reasoning that
    // makes the Email overload refuse a string does not apply.
    public IReadOnlyList<byte[]> ComputeCandidates(string presentedToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presentedToken);
        return _blindIndex.ComputeCandidates(presentedToken, Context);
    }
}

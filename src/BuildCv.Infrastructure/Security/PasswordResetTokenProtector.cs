using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Security.Encryption;

namespace BuildCv.Infrastructure.Security;

/// <summary>
/// The password-reset token: <c>&lt;payload&gt;.&lt;signature&gt;</c>, base64url, a fixed 25-byte payload
/// bound to an account and to that account's current password hash.
/// </summary>
/// <remarks>
/// <para>
/// It signs through the blind-index ring, exactly as <see cref="ImportEvidenceProtector"/> does, and for
/// the same three reasons: the length-prefixed context framing, one place in this codebase that touches
/// HMAC keys, and the write-active-key / read-EVERY-key rotation rule a signature needs. A token minted
/// the moment before a key roll must still verify after it, or a key rotation locks out everyone who was
/// mid-reset.
/// </para>
/// <para>
/// ITS OWN CONTEXT STRING, never the import one. The context is the domain separator: sharing it would let
/// a token minted for one purpose verify for the other, and these two grant very different things — one
/// says something about a PDF, the other sets a password.
/// </para>
/// <para>
/// THE PASSWORD HASH IS PART OF THE SIGNED INPUT, which is what makes the token single-use with nothing
/// stored. Using it changes the hash, so it stops verifying; so does changing the password any other way,
/// and so does a second reset. There is no "used" column to write, nothing to reap, and no race between
/// the two.
/// </para>
/// </remarks>
public sealed class PasswordResetTokenProtector : IPasswordResetTokenProtector
{
    public const string Context = "Identity.PasswordReset";

    private const byte CurrentVersion = 1;

    // version(1) + accountId(16) + issuedAt(8)
    private const int PayloadLength = 25;
    private const int AccountOffset = 1;
    private const int IssuedAtOffset = 17;

    private const char SegmentSeparator = '.';

    private readonly IBlindIndex _blindIndex;
    private readonly TimeProvider _timeProvider;

    public PasswordResetTokenProtector(IBlindIndex blindIndex, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(blindIndex);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _blindIndex = blindIndex;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// One hour. Short because the token IS a credential for the account: anyone holding it can take it
    /// over, and it travels through a mailbox this product does not control.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public string Protect(AccountId accountId, string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var payload = new byte[PayloadLength];
        payload[0] = CurrentVersion;
        accountId.Value.TryWriteBytes(payload.AsSpan(AccountOffset, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(
            payload.AsSpan(IssuedAtOffset, 8), _timeProvider.GetUtcNow().ToUnixTimeSeconds());

        var encodedPayload = Base64Url.EncodeToString(payload);
        return $"{encodedPayload}{SegmentSeparator}{Sign(encodedPayload, passwordHash)}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the id out of an UNVERIFIED payload, which the import protector never does. It is unavoidable
    /// here — the caller has forgotten their password, so the token is the only thing naming an account,
    /// and the signature cannot be checked until that account's hash is in hand. What an attacker gets by
    /// forging one is a single indexed database read; the signature then fails against the real hash.
    /// </remarks>
    public AccountId? ReadUnverifiedAccountId(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var separator = token.IndexOf(SegmentSeparator, StringComparison.Ordinal);
        if (separator <= 0 || separator == token.Length - 1)
            return null;

        var encodedPayload = token[..separator];

        // IsValid BEFORE the decode: Base64Url.TryDecodeFromChars throws FormatException on a bad
        // character despite the Try in its name, and an exception escaping here would be a 500 for
        // somebody who mistyped a link out of an email.
        if (!Base64Url.IsValid(encodedPayload))
            return null;

        var payload = Base64Url.DecodeFromChars(encodedPayload);
        if (payload.Length != PayloadLength || payload[0] != CurrentVersion)
            return null;

        return new AccountId(new Guid(payload.AsSpan(AccountOffset, 16), bigEndian: true));
    }

    public Result Verify(string token, AccountId accountId, string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(accountId);

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(passwordHash))
            return Invalid();

        var separator = token.IndexOf(SegmentSeparator, StringComparison.Ordinal);
        if (separator <= 0 || separator == token.Length - 1)
            return Invalid();

        var encodedPayload = token[..separator];
        var encodedSignature = token[(separator + 1)..];

        if (!Base64Url.IsValid(encodedSignature) || !Base64Url.IsValid(encodedPayload))
            return Invalid();

        var signature = Base64Url.DecodeFromChars(encodedSignature);

        // EVERY configured key, not just the active one. Compute alone would refuse every token minted
        // before a key roll for the whole rotation window, arriving at the user as "this link is invalid"
        // for a link that was valid when they clicked it.
        var accepted = false;
        foreach (var candidate in _blindIndex.ComputeCandidates(SigningInput(encodedPayload, passwordHash), Context))
            accepted |= CryptographicOperations.FixedTimeEquals(candidate, signature);

        if (!accepted)
            return Invalid();

        // Only now are the bytes ours to read. The account is re-checked from the signed payload rather
        // than trusted from the argument: the caller found the account by calling
        // ReadUnverifiedAccountId, and this is the line that turns that guess into a fact.
        var payload = Base64Url.DecodeFromChars(encodedPayload);
        if (payload.Length != PayloadLength || payload[0] != CurrentVersion)
            return Invalid();

        if (new Guid(payload.AsSpan(AccountOffset, 16), bigEndian: true) != accountId.Value)
            return Invalid();

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(IssuedAtOffset, 8)));

        // One-sided, as on the import token: a token dated in the future can only mean this server's clock
        // moved backwards between minting and verifying, and refusing our own signature over an NTP
        // correction would lock somebody out of their own account for our mistake.
        if (_timeProvider.GetUtcNow() - issuedAt > Lifetime)
            return Invalid();

        return Result.Success();
    }

    // The base64url TEXT is signed, not the bytes behind it, so verification never re-encodes a decoded
    // payload and can never disagree with the signer over a canonical form. The hash is appended under a
    // separator that cannot occur in base64url, so no pair of (payload, hash) values can be rearranged
    // into another pair with the same signing input.
    private string Sign(string encodedPayload, string passwordHash) =>
        Base64Url.EncodeToString(_blindIndex.Compute(SigningInput(encodedPayload, passwordHash), Context));

    private static string SigningInput(string encodedPayload, string passwordHash) =>
        $"{encodedPayload}{SegmentSeparator}{passwordHash}";

    // ONE MESSAGE FOR EVERY FAILURE, and it is not an accident of laziness. Distinguishing "expired" from
    // "not a real token" from "wrong account" would let anyone holding a link learn which of those it is,
    // and the expiry case in particular tells an attacker that the address they guessed HAS an account.
    private static Result Invalid() =>
        Result.Failure("This password reset link is invalid or has expired. Request a new one.");
}

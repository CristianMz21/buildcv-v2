using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Security.Encryption;

namespace BuildCv.Infrastructure.Security;

/// <summary>
/// The signed pass-through: <c>&lt;payload&gt;.&lt;signature&gt;</c>, both base64url, where the payload
/// is a fixed 35-byte record of one document's import signals bound to one account and one issuing
/// moment.
/// </summary>
/// <remarks>
/// <para>
/// IT REUSES THE BLIND-INDEX RING RATHER THAN GROWING A THIRD SECRET. Three properties come with that
/// and each of them is one this token needs: the framing is
/// <c>HMAC(key, int32BE(len(context)) || context || value)</c>, which makes the context an unforgeable
/// domain separator rather than a comment; writes take the ACTIVE key while reads try EVERY configured
/// key, which is exactly the behaviour a signature needs during a rotation window — a token minted before
/// a key roll must still verify after it, or every review screen open at that moment loses its evidence;
/// and <see cref="HmacBlindIndex"/> stays the only type in this assembly that handles a blind-index
/// secret, which is one place to get the framing and the rotation wrong instead of two. (The JWT signing
/// key is separate and unrelated — a different secret, a different ring, and `TokenService` owns it.)
/// </para>
/// <para>
/// It owns its CONTEXT STRING the way <c>AccountEmailIndex</c> and <c>RefreshTokenIndex</c> own theirs,
/// and for the same reason: the context is what stops a digest minted for one purpose being accepted for
/// another. A blind-index digest of an email can never be presented here, and this token can never
/// satisfy an account lookup.
/// </para>
/// <para>
/// THE ORDER OF THE CHECKS IS THE SECURITY PROPERTY. Everything in the payload is attacker-supplied
/// until the signature verifies, so nothing is read out of it and acted on before then — the account and
/// the issuing time are compared only after the bytes have been proven to be ours.
/// </para>
/// </remarks>
public sealed class ImportEvidenceProtector : IImportEvidenceProtector
{
    /// <summary>
    /// The AAD for every signature this type produces. It must not collide with any other context in the
    /// process; <c>ImportEvidenceProtectorTests</c> asserts a digest minted under another one is refused.
    /// </summary>
    public const string Context = "Resume.ImportEvidence";

    /// <summary>
    /// Bumped only when the payload LAYOUT changes. An old version byte is refused rather than guessed
    /// at, which costs a candidate one re-upload and cannot mis-read a field as a different one.
    /// </summary>
    private const byte CurrentVersion = 1;

    // 1 version + 16 account + 8 issued-at + 1 layout + 1 text layer + 4 page count + 4 warnings.
    private const int PayloadLength = 35;
    private const int AccountOffset = 1;
    private const int IssuedAtOffset = 17;
    private const int LayoutOffset = 25;
    private const int TextLayerOffset = 26;
    private const int PageCountOffset = 27;
    private const int WarningsOffset = 31;

    // A page count is never negative — ImportSignals refuses one — so the sentinel cannot collide with a
    // real value, and the payload stays fixed-width rather than growing a presence byte.
    private const int AbsentPageCount = -1;

    private const char SegmentSeparator = '.';

    private readonly IBlindIndex _blindIndex;
    private readonly TimeProvider _timeProvider;

    public ImportEvidenceProtector(IBlindIndex blindIndex, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(blindIndex);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _blindIndex = blindIndex;
        _timeProvider = timeProvider;
    }

    public string Protect(ImportSignals signals, AccountId accountId)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(accountId);

        var payload = new byte[PayloadLength];
        payload[0] = CurrentVersion;
        accountId.Value.TryWriteBytes(payload.AsSpan(AccountOffset, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(
            payload.AsSpan(IssuedAtOffset, 8), _timeProvider.GetUtcNow().ToUnixTimeSeconds());
        payload[LayoutOffset] = (byte)signals.ColumnLayout;
        payload[TextLayerOffset] = signals.HadTextLayer ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(PageCountOffset, 4), signals.PageCount ?? AbsentPageCount);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(WarningsOffset, 4), (int)signals.Warnings);

        // The base64url text IS what gets signed, not the bytes behind it, so verification never has to
        // re-encode a decoded payload and can never disagree with the signer over a canonical form.
        var encodedPayload = Base64Url.EncodeToString(payload);
        var signature = Base64Url.EncodeToString(_blindIndex.Compute(encodedPayload, Context));
        return $"{encodedPayload}{SegmentSeparator}{signature}";
    }

    public Result<ImportSignals> Unprotect(string token, AccountId accountId)
    {
        ArgumentNullException.ThrowIfNull(accountId);

        if (string.IsNullOrWhiteSpace(token))
            return Invalid();

        var separator = token.IndexOf(SegmentSeparator, StringComparison.Ordinal);
        if (separator <= 0 || separator == token.Length - 1)
            return Invalid();

        var encodedPayload = token[..separator];
        var encodedSignature = token[(separator + 1)..];

        // IsValid BEFORE any decode, both times: Base64Url.TryDecodeFromChars throws FormatException on a
        // bad character despite the Try in its name — the same trap Cursor.TryParse documents — and an
        // exception escaping here would be a 500 for a client that sent a mistyped token.
        if (!Base64Url.IsValid(encodedSignature) || !Base64Url.IsValid(encodedPayload))
            return Invalid();

        var signature = Base64Url.DecodeFromChars(encodedSignature);

        // FIRST, and against EVERY configured key. Compute alone would silently refuse every token minted
        // under the previous key for the whole of a rotation window — the same read-path bug the blind
        // index documents, arriving here as "your upload evidence is not valid" for anyone who happened
        // to be reviewing a CV when the key rolled.
        var accepted = false;
        foreach (var candidate in _blindIndex.ComputeCandidates(encodedPayload, Context))
            accepted |= CryptographicOperations.FixedTimeEquals(candidate, signature);

        if (!accepted)
            return Invalid();

        // Only now are the bytes ours to read.
        var payload = Base64Url.DecodeFromChars(encodedPayload);
        if (payload.Length != PayloadLength || payload[0] != CurrentVersion)
            return Invalid();

        if (new Guid(payload.AsSpan(AccountOffset, 16), bigEndian: true) != accountId.Value)
            return Invalid();

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(IssuedAtOffset, 8)));

        // One-sided. A token dated in the FUTURE can only mean this server's own clock moved backwards
        // between minting and verifying, and refusing our own signature for that would deny a candidate
        // their evidence over an NTP correction; the token grants nothing beyond a claim about the
        // holder's own document, so there is no attack the other bound would close.
        if (_timeProvider.GetUtcNow() - issuedAt > IImportEvidenceProtector.Lifetime)
            return Result<ImportSignals>.Failure(IImportEvidenceProtector.ExpiredTokenError);

        var pageCount = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(PageCountOffset, 4));

        // Create validates the enums it is handed. The signature has already proven these bytes are ours,
        // so a value outside either enum would be a bug in Protect rather than an attack — and it would
        // be a durable one, because both are persisted into fixed-width columns with unchecked
        // conversions. Refused as an invalid token rather than thrown, so the blast radius is one import.
        try
        {
            return Result<ImportSignals>.Success(ImportSignals.Create(
                (ColumnLayout)payload[LayoutOffset],
                payload[TextLayerOffset] != 0,
                pageCount == AbsentPageCount ? null : pageCount,
                (ImportWarningFlags)BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(WarningsOffset, 4))));
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
    }

    private static Result<ImportSignals> Invalid() =>
        Result<ImportSignals>.Failure(IImportEvidenceProtector.InvalidTokenError);
}

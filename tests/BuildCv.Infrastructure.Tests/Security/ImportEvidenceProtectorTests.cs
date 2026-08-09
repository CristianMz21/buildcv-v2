using System.Buffers.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Security;
using BuildCv.Infrastructure.Security.Encryption;
using BuildCv.Infrastructure.Tests.Security.Encryption;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Security;

// The signature itself. Everything above this file trusts one sentence — "a client cannot forge import
// signals" — and this is where that sentence is either true or a comment.
public class ImportEvidenceProtectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly TestTimeProvider _clock = new(Now);
    private readonly ImportEvidenceProtector _protector;
    private readonly AccountId _owner = AccountId.New();

    public ImportEvidenceProtectorTests() => _protector = TestImportEvidence.Protector(_clock);

    private static ImportSignals Signals(
        ColumnLayout layout = ColumnLayout.Multiple,
        bool hadTextLayer = true,
        int? pageCount = 3,
        ImportWarningFlags warnings = ImportWarningFlags.None) =>
        ImportSignals.Create(layout, hadTextLayer, pageCount, warnings);

    // Every field survives, including the two that have a sentinel or a default in the payload: a null
    // page count is written as -1 and must come back null, not as -1.
    [Theory]
    [InlineData(ColumnLayout.Unknown, true, null, ImportWarningFlags.None)]
    [InlineData(ColumnLayout.Single, true, 1, ImportWarningFlags.None)]
    [InlineData(ColumnLayout.Multiple, false, 9, ImportWarningFlags.NoTextContent)]
    [InlineData(ColumnLayout.Single, false, 0, ImportWarningFlags.NoTextContent)]
    public void ProtectThenUnprotect_RoundTripsEveryField(
        ColumnLayout layout, bool hadTextLayer, int? pageCount, ImportWarningFlags warnings)
    {
        var original = Signals(layout, hadTextLayer, pageCount, warnings);

        var result = _protector.Unprotect(_protector.Protect(original, _owner), _owner);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Should().Be(original);
    }

    // THE SIGNATURE CHECK, against the forgery someone would actually attempt: the real token for a
    // two-column upload with its LAYOUT BYTE rewritten to Single, keeping the original signature.
    //
    // Written this way after the negative control caught the earlier version lying. That one flipped the
    // FIRST character of the payload, which is the version byte — so it was refused by the version guard
    // and stayed green with the signature check deleted. It asserted the right message for the wrong
    // reason, which is the failure mode where an assertion is a small closed value.
    //
    // Here every other guard passes by construction: the length is unchanged, the version byte is
    // untouched, the account is the caller's and the token is seconds old. Only the signature can refuse
    // it, which is what makes this a control on the signature.
    [Fact]
    public void Unprotect_APayloadWithARewrittenLayout_IsRefused()
    {
        var forged = WithLayoutByteRewritten(
            _protector.Protect(Signals(ColumnLayout.Multiple), _owner), (byte)ColumnLayout.Single);

        _protector.Unprotect(forged, _owner).Error.Should().Be(IImportEvidenceProtector.InvalidTokenError);
    }

    // And the same rewrite WITH a fresh signature really is a different set of signals, so the test above
    // is about the signature and not about a layout byte that never mattered.
    [Fact]
    public void ARewrittenLayout_WouldHaveChangedTheSignals_HadItBeenSigned()
    {
        var forged = WithLayoutByteRewritten(
            _protector.Protect(Signals(ColumnLayout.Multiple), _owner), (byte)ColumnLayout.Single);

        var resigned = Resign(forged[..forged.IndexOf('.', StringComparison.Ordinal)]);

        _protector.Unprotect(resigned, _owner).Value!.ColumnLayout.Should().Be(ColumnLayout.Single);
    }

    [Fact]
    public void Unprotect_ATamperedSignature_IsRefused()
    {
        var token = _protector.Protect(Signals(), _owner);
        var separator = token.IndexOf('.', StringComparison.Ordinal);
        var tampered = token[..(separator + 1)] + Mutate(token[(separator + 1)..]);

        _protector.Unprotect(tampered, _owner).Error.Should().Be(IImportEvidenceProtector.InvalidTokenError);
    }

    // A HAND-WRITTEN PAYLOAD WITH NO SIGNATURE AT ALL, which is what a client that read the wire format
    // would try first. The negative control for the whole scheme: delete the verification and this is
    // the test that goes green.
    [Fact]
    public void Unprotect_AnUnsignedPayload_IsRefused()
    {
        var token = _protector.Protect(Signals(), _owner);
        var payload = token[..token.IndexOf('.', StringComparison.Ordinal)];

        _protector.Unprotect(payload, _owner).Error.Should().Be(IImportEvidenceProtector.InvalidTokenError);
        _protector.Unprotect($"{payload}.", _owner).Error.Should().Be(IImportEvidenceProtector.InvalidTokenError);
        _protector.Unprotect($"{payload}.{payload}", _owner).Error
            .Should().Be(IImportEvidenceProtector.InvalidTokenError);
    }

    // THE ACCOUNT BINDING, against a token that is valid in every other respect: correctly signed by
    // this protector, minted a second ago, and structurally perfect.
    [Fact]
    public void Unprotect_ATokenIssuedToAnotherAccount_IsRefused()
    {
        var token = _protector.Protect(Signals(), AccountId.New());

        _protector.Unprotect(token, _owner).Error.Should().Be(IImportEvidenceProtector.InvalidTokenError);
    }

    // THE EXPIRY, both sides of the boundary, so the test distinguishes "expires" from "always expires"
    // and from "never expires".
    [Fact]
    public void Unprotect_JustInsideTheLifetime_IsAccepted()
    {
        var token = _protector.Protect(Signals(), _owner);
        _clock.Advance(IImportEvidenceProtector.Lifetime);

        _protector.Unprotect(token, _owner).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Unprotect_PastTheLifetime_IsRefusedWithTheExpiredMessage()
    {
        var token = _protector.Protect(Signals(), _owner);
        _clock.Advance(IImportEvidenceProtector.Lifetime + TimeSpan.FromSeconds(1));

        _protector.Unprotect(token, _owner).Error.Should().Be(IImportEvidenceProtector.ExpiredTokenError);
    }

    // THE ROTATION WINDOW, which is the whole reason verification uses ComputeCandidates. A token minted
    // under b1 has to keep verifying after b2 becomes active, or every review screen open at the moment
    // of a key roll loses its evidence.
    [Fact]
    public void Unprotect_AfterAKeyRotation_StillAcceptsATokenMintedUnderTheRetiredKey()
    {
        var mintedUnderB1 = TestImportEvidence
            .Protector(EncryptionTestKeys.SingleBlindIndexRing("b1"), _clock)
            .Protect(Signals(), _owner);

        var afterRotation = TestImportEvidence.Protector(
            EncryptionTestKeys.BlindIndexRing("b2", "b2", "b1"), _clock);

        afterRotation.Unprotect(mintedUnderB1, _owner).IsSuccess.Should().BeTrue();
    }

    // And once the retired key is really gone, the old token stops verifying — which is what makes the
    // test above evidence of a candidate list rather than of a signature nobody checks.
    [Fact]
    public void Unprotect_AfterTheRetiredKeyIsDropped_RefusesTheOldToken()
    {
        var mintedUnderB1 = TestImportEvidence
            .Protector(EncryptionTestKeys.SingleBlindIndexRing("b1"), _clock)
            .Protect(Signals(), _owner);

        var b2Only = TestImportEvidence.Protector(EncryptionTestKeys.SingleBlindIndexRing("b2"), _clock);

        b2Only.Unprotect(mintedUnderB1, _owner).Error.Should().Be(IImportEvidenceProtector.InvalidTokenError);
    }

    // THE CONTEXT STRING IS THE DOMAIN SEPARATOR. A digest computed over the same payload under a
    // different AAD — an email blind index, say — must not satisfy this check, or the context is
    // decoration and any HMAC this process can produce is a valid signature.
    [Fact]
    public void Unprotect_ASignatureComputedUnderAnotherContext_IsRefused()
    {
        var token = _protector.Protect(Signals(), _owner);
        var payload = token[..token.IndexOf('.', StringComparison.Ordinal)];

        var blindIndex = new HmacBlindIndex(EncryptionTestKeys.SingleBlindIndexRing());
        var wrongContext = Base64Url.EncodeToString(blindIndex.Compute(payload, "Account.Email"));

        _protector.Unprotect($"{payload}.{wrongContext}", _owner).Error
            .Should().Be(IImportEvidenceProtector.InvalidTokenError);
    }

    // Nothing a client can type reaches an exception. Base64Url.DecodeFromChars throws FormatException
    // on a bad character despite the Try in its sibling's name, so an unguarded decode here would be a
    // 500 for a mistyped token rather than a field error.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-separator")]
    [InlineData(".")]
    [InlineData(".signature-only")]
    [InlineData("payload-only.")]
    [InlineData("not base64!.also not base64!")]
    [InlineData("AAAA.====")]
    [InlineData(" . ")]
    public void Unprotect_Garbage_IsRefusedWithoutThrowing(string token)
    {
        var act = () => _protector.Unprotect(token, _owner);

        act.Should().NotThrow();
        act().Error.Should().Be(IImportEvidenceProtector.InvalidTokenError);
    }

    // A payload of the right length signed correctly but carrying an unknown version byte is refused
    // rather than read as version 1 — a layout change must cost one re-upload, not a mis-read field.
    [Fact]
    public void Unprotect_AnUnknownVersionByte_IsRefused()
    {
        var token = _protector.Protect(Signals(), _owner);
        var payload = Base64Url.DecodeFromChars(token.AsSpan()[..token.IndexOf('.', StringComparison.Ordinal)]);
        payload[0] = 99;

        var reencoded = Base64Url.EncodeToString(payload);
        var blindIndex = new HmacBlindIndex(EncryptionTestKeys.SingleBlindIndexRing());
        var signature = Base64Url.EncodeToString(
            blindIndex.Compute(reencoded, ImportEvidenceProtector.Context));

        _protector.Unprotect($"{reencoded}.{signature}", _owner).Error
            .Should().Be(IImportEvidenceProtector.InvalidTokenError);
    }

    // The token is opaque, but "opaque" must not mean "the account id is in the clear next to it": a
    // reader with the token still has a base64 of the owner's guid. Stated rather than fixed — the token
    // is handed to the account it names and to nobody else — and pinned so a future change that started
    // putting document TEXT in the payload has to argue with this test.
    [Fact]
    public void Protect_ProducesAFixedSizeTokenThatCannotGrowWithTheDocument()
    {
        var shortest = _protector.Protect(Signals(ColumnLayout.Unknown, true, null), _owner);
        var longest = _protector.Protect(
            Signals(ColumnLayout.Multiple, false, int.MaxValue, ImportWarningFlags.NoTextContent), _owner);

        longest.Length.Should().Be(shortest.Length,
            "every field is fixed-width, so nothing about the document can change the token's size");
    }

    // Changes one character of a base64url segment to a different legal one, so the mutation is a
    // tampered VALUE rather than a decode failure — the check under test is the signature, not the
    // encoding guard.
    private static string Mutate(string segment)
    {
        var characters = segment.ToCharArray();
        characters[0] = characters[0] == 'A' ? 'B' : 'A';
        return new string(characters);
    }

    // Rewrites the payload's column-layout byte and keeps the signature that was minted for the original.
    // The layout lives at offset 25 of the 35-byte payload — well past the version byte and the account
    // guid — so nothing but the signature can notice.
    private static string WithLayoutByteRewritten(string token, byte layout)
    {
        var separator = token.IndexOf('.', StringComparison.Ordinal);
        var payload = Base64Url.DecodeFromChars(token.AsSpan()[..separator]);
        payload[25] = layout;

        return Base64Url.EncodeToString(payload) + token[separator..];
    }

    private static string Resign(string encodedPayload)
    {
        var blindIndex = new HmacBlindIndex(EncryptionTestKeys.SingleBlindIndexRing());
        return $"{encodedPayload}."
            + Base64Url.EncodeToString(blindIndex.Compute(encodedPayload, ImportEvidenceProtector.Context));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}

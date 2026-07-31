using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Security.Encryption;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence;

// The second routed finding: IBlindIndex.Compute takes its value verbatim, so the digest is only
// stable if every caller passes the same NORMALIZED form. Nothing about the raw interface enforces
// that, and the failure mode is a silent zero-row read on an auth path.
public sealed class BlindIndexPairingTests
{
    private const string MixedCaseEmail = "USER@Example.com";
    private const string LowerCaseEmail = "user@example.com";

    private static AccountEmailIndex EmailIndex() => new(PersistenceTestContext.BlindIndex());

    // The assertion the finding asked for. Registration takes the address the user typed; login takes
    // whatever they typed the next time. Both go through Email.Create, which lower-cases, so both
    // reach the same row.
    [Fact]
    public void Compute_RegistrationAndLoginPaths_ProduceIdenticalDigest()
    {
        var index = EmailIndex();

        var registration = index.Compute(Email.Create(MixedCaseEmail));
        var login = index.Compute(Email.Create(LowerCaseEmail));

        registration.Should().Equal(login);
    }

    // Why the typed parameter exists rather than a comment asking callers to be careful. Handing the
    // raw request string to IBlindIndex directly is a compiling, plausible-looking call that writes
    // one digest and looks up another.
    [Fact]
    public void Compute_RawUnnormalizedString_DisagreesWithTheTypedHelper()
    {
        var blindIndex = PersistenceTestContext.BlindIndex();
        var index = new AccountEmailIndex(blindIndex);

        var throughTheDomain = index.Compute(Email.Create(MixedCaseEmail));
        var rawRequestValue = blindIndex.Compute(MixedCaseEmail, AccountEmailIndex.Context);

        rawRequestValue.Should().NotEqual(throughTheDomain,
            "this is exactly the bug the typed helper makes impossible to write");
    }

    [Fact]
    public void ComputeCandidates_LeadsWithTheDigestThatWouldBeWritten()
    {
        var index = EmailIndex();
        var email = Email.Create(MixedCaseEmail);

        index.ComputeCandidates(email).Should().NotBeEmpty()
            .And.Subject.First().Should().Equal(index.Compute(email));
    }

    [Fact]
    public void RefreshTokenIndex_MatchesAPresentedTokenAgainstItsStoredDigest()
    {
        var index = new RefreshTokenIndex(PersistenceTestContext.BlindIndex());
        var token = new string('a', 43);
        var refreshToken = RefreshToken.Create(
            token, AccountId.New(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));

        index.ComputeCandidates(token).Should().ContainEquivalentOf(index.Compute(refreshToken));
    }

    // Different columns must never share a context, or an envelope lifted from one column would
    // authenticate in the other and a digest would collide across tables.
    [Fact]
    public void Contexts_AreDistinctPerColumn() =>
        AccountEmailIndex.Context.Should().NotBe(RefreshTokenIndex.Context);

    // Keeps EncryptedColumn.EnvelopeOverhead honest. If the envelope format grows, the pinned column
    // widths silently become too small and the first oversized value is truncated at insert — which
    // destroys the row, because the GCM tag lives in the last 16 bytes.
    [Fact]
    public void EnvelopeOverhead_CoversWhatTheEncryptorActuallyEmits()
    {
        var encryptor = PersistenceTestContext.Encryptor();
        var plaintext = new string('x', 100);

        var envelope = encryptor.Encrypt(plaintext, "Account.Email");

        (envelope.Length - plaintext.Length).Should().BeLessThanOrEqualTo(EncryptedColumn.EnvelopeOverhead);
    }

    // The design-time factory bypasses AddInfrastructure, so nothing else would notice if its dummy
    // ring stopped satisfying the validation the host runs. `dotnet ef migrations add` would then fail
    // on configuration rather than on anything to do with migrations.
    [Fact]
    public void DesignTimeSettings_SatisfyBothKeyRings()
    {
        var settings = BuildCvDbContextFactory.DesignTimeSettings();

        EncryptionKeyRing.Validate(settings).Should().BeNull();
        BlindIndexKeyRing.Validate(settings.BlindIndex).Should().BeNull();
    }
}

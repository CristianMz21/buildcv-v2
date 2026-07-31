using BuildCv.Infrastructure.Security.Encryption;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Security.Encryption;

public class HmacBlindIndexTests
{
    private const string Context = "Account.Email";
    private const string Value = "candidate@example.com";

    private readonly HmacBlindIndex _index = new(EncryptionTestKeys.SingleKeyRing());

    [Fact]
    public void Compute_ReturnsThirtyTwoBytes()
    {
        _index.Compute(Value, Context).Should().HaveCount(HmacBlindIndex.DigestSizeInBytes);
    }

    [Fact]
    public void Compute_SameValueAndContext_IsStableAcrossInstances()
    {
        // The lookup index is written by one instance and queried by another, possibly in another
        // process. If this ever stops holding, every login by email breaks.
        var other = new HmacBlindIndex(EncryptionTestKeys.SingleKeyRing());

        _index.Compute(Value, Context).Should().Equal(other.Compute(Value, Context));
    }

    [Fact]
    public void Compute_DifferentValue_ProducesADifferentDigest()
    {
        _index.Compute(Value, Context).Should().NotEqual(_index.Compute("recruiter@example.com", Context));
    }

    [Fact]
    public void Compute_DifferentContext_ProducesADifferentDigest()
    {
        _index.Compute(Value, Context).Should().NotEqual(_index.Compute(Value, "RefreshToken.Token"));
    }

    [Fact]
    public void Compute_ContextAndValueCannotRunTogetherIntoTheSameDigest()
    {
        // Without a separator "Account.Ema" + "il@x" and "Account.Email" + "@x" would hash the same
        // bytes and let a value in one column satisfy a lookup in another.
        _index.Compute("il@x", "Account.Ema").Should().NotEqual(_index.Compute("@x", "Account.Email"));
    }

    [Fact]
    public void Compute_DifferentKeyRing_ProducesADifferentDigest()
    {
        var otherRing = new HmacBlindIndex(EncryptionTestKeys.SingleKeyRing("other"));

        _index.Compute(Value, Context).Should().NotEqual(otherRing.Compute(Value, Context));
    }

    [Fact]
    public void Compute_UsesTheActiveKey_SoRotatingTheHmacSecretInvalidatesStoredDigests()
    {
        // Documented consequence of a keyless 32-byte digest: rotating the HMAC secret requires
        // recomputing every stored blind index. Rotate the AES secret on its own when only payload
        // keys need to move.
        var beforeRotation = new HmacBlindIndex(EncryptionTestKeys.Ring("v1", "v1", "v2"));
        var afterRotation = new HmacBlindIndex(EncryptionTestKeys.Ring("v2", "v1", "v2"));

        beforeRotation.Compute(Value, Context).Should().NotEqual(afterRotation.Compute(Value, Context));
    }

    [Fact]
    public void Compute_TakesTheValueVerbatim_LeavingNormalizationToTheDomain()
    {
        // Email lower-cases in its own factory; a second normalization here would let this type and
        // the Domain disagree about what equality means.
        _index.Compute("Candidate@Example.com", Context).Should().NotEqual(_index.Compute(Value, Context));
    }

    [Fact]
    public void Compute_NullValue_Throws()
    {
        var act = () => _index.Compute(null!, Context);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compute_MissingContext_Throws(string? context)
    {
        var act = () => _index.Compute(Value, context!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullKeyRing_Throws()
    {
        var act = () => new HmacBlindIndex(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

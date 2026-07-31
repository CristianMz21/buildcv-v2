using BuildCv.Infrastructure.Security.Encryption;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Security.Encryption;

public class HmacBlindIndexTests
{
    private const string Context = "Account.Email";
    private const string Value = "candidate@example.com";

    private readonly HmacBlindIndex _index = new(EncryptionTestKeys.SingleBlindIndexRing());

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
        var other = new HmacBlindIndex(EncryptionTestKeys.SingleBlindIndexRing());

        _index.Compute(Value, Context).Should().Equal(other.Compute(Value, Context));
    }

    [Fact]
    public void Compute_IsUnaffectedByAesKeyRotation()
    {
        // THE regression test for the key-material split. When both secrets hung off one key id,
        // repointing Encryption:ActiveKeyId silently rehashed every lookup: WHERE EmailHash = @x
        // matched nothing, so login reported "account not found" AND re-registering an existing
        // address succeeded, because the new digest did not collide with the old one under the
        // unique index. Duplicate identities, and no exception anywhere.
        var beforeRotation = new HmacBlindIndex(
            new BlindIndexKeyRing(EncryptionTestKeys.Settings("v1", ["v1"], "b1", ["b1"])));
        var afterRotation = new HmacBlindIndex(
            new BlindIndexKeyRing(EncryptionTestKeys.Settings("v2", ["v1", "v2"], "b1", ["b1"])));

        afterRotation.Compute(Value, Context).Should().Equal(beforeRotation.Compute(Value, Context));
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
        // The length prefix makes the split unambiguous by construction: without it,
        // ("Account.Ema", "il@x") and ("Account.Email", "@x") hash the same bytes and a value in one
        // column satisfies a lookup in another.
        _index.Compute("il@x", "Account.Ema").Should().NotEqual(_index.Compute("@x", "Account.Email"));
    }

    [Fact]
    public void Compute_DifferentBlindIndexKey_ProducesADifferentDigest()
    {
        var otherRing = new HmacBlindIndex(EncryptionTestKeys.SingleBlindIndexRing("other"));

        _index.Compute(Value, Context).Should().NotEqual(otherRing.Compute(Value, Context));
    }

    [Fact]
    public void Compute_TakesTheValueVerbatim_LeavingNormalizationToTheDomain()
    {
        // Email lower-cases in its own factory; a second normalization here would let this type and
        // the Domain disagree about what equality means.
        _index.Compute("Candidate@Example.com", Context).Should().NotEqual(_index.Compute(Value, Context));
    }

    [Fact]
    public void ComputeCandidates_SingleKey_ReturnsJustTheActiveDigest()
    {
        _index.ComputeCandidates(Value, Context).Should().ContainSingle()
            .Which.Should().Equal(_index.Compute(Value, Context));
    }

    [Fact]
    public void ComputeCandidates_ReturnsOneDigestPerConfiguredKeyActiveFirst()
    {
        var duringRotation = new HmacBlindIndex(EncryptionTestKeys.BlindIndexRing("b2", "b1", "b2"));

        var candidates = duringRotation.ComputeCandidates(Value, Context);

        candidates.Should().HaveCount(2);
        candidates[0].Should().Equal(duringRotation.Compute(Value, Context));
        candidates[1].Should().NotEqual(candidates[0]);
    }

    [Fact]
    public void ComputeCandidates_DuringRotation_StillMatchesDigestsWrittenUnderThePreviousKey()
    {
        // Rotation window: add b2 -> deploy (write b2, read matches b2 or b1) -> backfill -> drop b1.
        var beforeRotation = new HmacBlindIndex(EncryptionTestKeys.SingleBlindIndexRing("b1"));
        var stored = beforeRotation.Compute(Value, Context);

        var duringRotation = new HmacBlindIndex(EncryptionTestKeys.BlindIndexRing("b2", "b1", "b2"));

        duringRotation.Compute(Value, Context).Should().NotEqual(stored, "new writes must use b2");
        duringRotation.ComputeCandidates(Value, Context)
            .Should().ContainSingle(candidate => candidate.SequenceEqual(stored),
                "a lookup must still find rows written under b1");
    }

    [Fact]
    public void ComputeCandidates_OnceTheOldKeyIsDropped_NoLongerMatchesItsDigests()
    {
        // The backfill has to finish before b1 leaves the ring; this is what going too fast costs.
        var stored = new HmacBlindIndex(EncryptionTestKeys.SingleBlindIndexRing("b1")).Compute(Value, Context);

        var afterDrop = new HmacBlindIndex(EncryptionTestKeys.SingleBlindIndexRing("b2"));

        afterDrop.ComputeCandidates(Value, Context)
            .Should().NotContain(candidate => candidate.SequenceEqual(stored));
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
    public void ComputeCandidates_NullValue_Throws()
    {
        var act = () => _index.ComputeCandidates(null!, Context);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullKeyRing_Throws()
    {
        var act = () => new HmacBlindIndex(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

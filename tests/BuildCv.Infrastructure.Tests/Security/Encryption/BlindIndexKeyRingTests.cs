using BuildCv.Infrastructure.Security.Encryption;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Security.Encryption;

public class BlindIndexKeyRingTests
{
    [Fact]
    public void Constructor_ValidSettings_ExposesItsOwnActiveKeyId()
    {
        var ring = EncryptionTestKeys.BlindIndexRing("b2", "b1", "b2");

        ring.ActiveKeyId.Should().Be("b2");
        ring.KeyIds.Should().BeEquivalentTo(["b2", "b1"]);
    }

    [Fact]
    public void KeyIds_PutTheActiveKeyFirst()
    {
        // Lookups walk the candidates in order and the active key is the common hit. "b1" sorts
        // first, so plain ordering would put the retired key ahead of the active one.
        EncryptionTestKeys.BlindIndexRing("b2", "b1", "b2").KeyIds.Should().Equal("b2", "b1");
    }

    [Fact]
    public void Constructor_ExactlyTwoKeys_IsAccepted()
    {
        // Two keys is one rotation in flight: writes go to the active key, reads still match the
        // retired one until the backfill finishes.
        EncryptionTestKeys.BlindIndexRing("b2", "b1", "b2").KeyIds.Should().HaveCount(BlindIndexKeyRing.MaxKeys);
    }

    [Fact]
    public void Constructor_MoreThanTwoKeys_ThrowsNamingTheKeysAndTheBackfill()
    {
        // A third live key means the previous rotation never completed, and every extra secret is
        // another value that can satisfy a uniqueness lookup — the same duplicate-identity surface
        // the shared-pointer bug opened, just arriving slower. Enforced at startup rather than
        // warned about: an unenforced warning about an unenforced operational step is the same gap
        // one layer down.
        var act = () => new BlindIndexKeyRing(
            EncryptionTestKeys.Settings("v1", ["v1"], "b3", ["b1", "b2", "b3"]).BlindIndex);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*b1, b2, b3*")
            .WithMessage("*at most 2 may be live at once*")
            .WithMessage("*Finish the backfill*");
    }

    [Fact]
    public void ActiveKeyId_IsIndependentOfTheAesActiveKeyId()
    {
        // The whole point of the split: repointing Encryption:ActiveKeyId must not move this one.
        var settings = EncryptionTestKeys.Settings("v2", ["v1", "v2"], "b1", ["b1"]);

        new BlindIndexKeyRing(settings.BlindIndex).ActiveKeyId.Should().Be("b1");
        new EncryptionKeyRing(settings).ActiveKeyId.Should().Be("v2");
    }

    [Fact]
    public void GetKey_ConfiguredKey_ReturnsThirtyTwoBytes()
    {
        EncryptionTestKeys.SingleBlindIndexRing().GetKey("b1").ToArray()
            .Should().HaveCount(BlindIndexKeyRing.KeySizeInBytes);
    }

    [Fact]
    public void GetKey_UnknownKeyId_Throws()
    {
        var ring = EncryptionTestKeys.SingleBlindIndexRing();

        var act = () => ring.GetKey("b9").ToArray();

        act.Should().Throw<EncryptionConfigurationException>().WithMessage("*b9*");
    }

    [Fact]
    public void GetKey_DoesNotReturnTheAesKeyForTheSameId()
    {
        // Structurally impossible now, but pinned: an index key must never be able to stand in for a
        // payload key, or the blast radius of leaking one becomes the blast radius of leaking both.
        var settings = EncryptionTestKeys.Settings("shared", ["shared"], "shared", ["shared"]);

        new BlindIndexKeyRing(settings.BlindIndex).GetKey("shared").ToArray()
            .Should().NotEqual(new EncryptionKeyRing(settings).GetAesKey("shared").ToArray());
    }

    [Fact]
    public void Constructor_NoKeysConfigured_Throws()
    {
        var act = () => new BlindIndexKeyRing(new BlindIndexSettings());

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:BlindIndex:Keys must contain at least one key*");
    }

    [Fact]
    public void Constructor_ActiveKeyIdIsMissing_Throws()
    {
        var act = () => new BlindIndexKeyRing(EncryptionTestKeys.Settings("v1", ["v1"], string.Empty, ["b1"]).BlindIndex);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:BlindIndex:ActiveKeyId must be configured*");
    }

    [Fact]
    public void Constructor_ActiveKeyIdIsNotInKeys_Throws()
    {
        var act = () => new BlindIndexKeyRing(EncryptionTestKeys.Settings("v1", ["v1"], "b9", ["b1"]).BlindIndex);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:BlindIndex:ActiveKeyId 'b9'*");
    }

    [Fact]
    public void Constructor_KeyMaterialIsNotBase64_ThrowsNamingTheOffendingKeyId()
    {
        var settings = EncryptionTestKeys.Settings("v1", ["v1"], "b1", ["b1"]);
        settings.BlindIndex.Keys["b1"] = "this is not base64!!";

        var act = () => new BlindIndexKeyRing(settings.BlindIndex);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:BlindIndex:Keys:b1*")
            .WithMessage("*not valid base64*");
    }

    [Fact]
    public void Constructor_KeyMaterialDecodesToWrongLength_Throws()
    {
        var settings = EncryptionTestKeys.Settings("v1", ["v1"], "b1", ["b1"]);
        settings.BlindIndex.Keys["b1"] = Convert.ToBase64String(new byte[16]);

        var act = () => new BlindIndexKeyRing(settings.BlindIndex);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:BlindIndex:Keys:b1*")
            .WithMessage("*32 bytes*");
    }

    [Theory]
    [InlineData("tenant:b1")]
    [InlineData("tenant--b1")]
    public void Constructor_KeyIdThatWouldCorruptAKeyVaultPath_Throws(string keyId)
    {
        var act = () => new BlindIndexKeyRing(EncryptionTestKeys.Settings("v1", ["v1"], keyId, [keyId]).BlindIndex);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:BlindIndex:Keys*");
    }

    [Fact]
    public void Settings_AreNeverRenderedWithTheirKeyMaterial()
    {
        var settings = EncryptionTestKeys.Settings("v1", ["v1"], "b1", ["b1"]);

        settings.BlindIndex.ToString().Should().NotContain(EncryptionTestKeys.Secret("b1:blind"));
        settings.BlindIndex.ToString().Should().Contain("b1");
    }
}

using BuildCv.Infrastructure.Security.Encryption;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Security.Encryption;

public class EncryptionKeyRingTests
{
    [Fact]
    public void Constructor_ValidSettings_ExposesActiveKeyIdAndEveryConfiguredKey()
    {
        var ring = EncryptionTestKeys.Ring("v2", "v1", "v2");

        ring.ActiveKeyId.Should().Be("v2");
        ring.ContainsKey("v1").Should().BeTrue();
        ring.ContainsKey("v2").Should().BeTrue();
        ring.ContainsKey("v3").Should().BeFalse();
    }

    [Fact]
    public void GetAesKey_ConfiguredKey_ReturnsThirtyTwoBytesDistinctFromTheHmacKey()
    {
        var ring = EncryptionTestKeys.SingleKeyRing();

        var aes = ring.GetAesKey("v1").ToArray();
        var hmac = ring.GetHmacKey("v1").ToArray();

        aes.Should().HaveCount(EncryptionKeyRing.KeySizeInBytes);
        hmac.Should().HaveCount(EncryptionKeyRing.KeySizeInBytes);
        aes.Should().NotEqual(hmac);
    }

    [Fact]
    public void GetAesKey_UnknownKeyId_Throws()
    {
        var ring = EncryptionTestKeys.SingleKeyRing();

        var act = () => ring.GetAesKey("retired-v0").ToArray();

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*retired-v0*");
    }

    [Fact]
    public void Constructor_KeyMaterialIsNotBase64_ThrowsNamingTheOffendingKeyId()
    {
        var settings = EncryptionTestKeys.Settings("v1", "v1");
        settings.Keys["v1"] = settings.Keys["v1"] with { Aes = "this is not base64!!" };

        var act = () => new EncryptionKeyRing(settings);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:Keys:v1:Aes*")
            .WithMessage("*not valid base64*");
    }

    [Fact]
    public void Constructor_KeyMaterialDecodesToWrongLength_ThrowsNamingTheOffendingKeyId()
    {
        var settings = EncryptionTestKeys.Settings("v1", "v1");
        settings.Keys["v1"] = settings.Keys["v1"] with { Hmac = Convert.ToBase64String(new byte[16]) };

        var act = () => new EncryptionKeyRing(settings);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:Keys:v1:Hmac*")
            .WithMessage("*32 bytes*");
    }

    [Fact]
    public void Constructor_ActiveKeyIdIsNotInKeys_Throws()
    {
        var settings = EncryptionTestKeys.Settings("v9", "v1");

        var act = () => new EncryptionKeyRing(settings);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*ActiveKeyId 'v9'*");
    }

    [Fact]
    public void Constructor_ActiveKeyIdIsMissing_Throws()
    {
        var settings = EncryptionTestKeys.Settings(string.Empty, "v1");

        var act = () => new EncryptionKeyRing(settings);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*ActiveKeyId must be configured*");
    }

    [Fact]
    public void Constructor_NoKeysConfigured_Throws()
    {
        var act = () => new EncryptionKeyRing(new EncryptionSettings { ActiveKeyId = "v1" });

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Keys must contain at least one key*");
    }

    [Fact]
    public void Constructor_EmptyKeyMaterial_Throws()
    {
        var settings = EncryptionTestKeys.Settings("v1", "v1");
        settings.Keys["v1"] = settings.Keys["v1"] with { Aes = string.Empty };

        var act = () => new EncryptionKeyRing(settings);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:Keys:v1:Aes must be configured*");
    }

    [Fact]
    public void Constructor_NonAsciiKeyId_Throws()
    {
        // Key ids go into the envelope as raw ASCII; a non-ASCII id would round-trip as '?' and
        // resolve to the wrong key on the way back.
        var settings = EncryptionTestKeys.Settings("clave-año", "clave-año");

        var act = () => new EncryptionKeyRing(settings);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*printable ASCII*");
    }

    [Fact]
    public void Settings_AreNeverRenderedWithTheirKeyMaterial()
    {
        // Options objects reach logs, debugger watches and exception dumps; the record-generated
        // ToString would print both secrets verbatim.
        var settings = EncryptionTestKeys.Settings("v1", "v1", "v2");

        settings.Keys["v1"].ToString().Should().Be("[redacted]");
        settings.ToString().Should().NotContain(EncryptionTestKeys.Secret("v1:aes"));
        settings.ToString().Should().NotContain(EncryptionTestKeys.Secret("v1:hmac"));
        settings.ToString().Should().Contain("v1").And.Contain("v2");
    }

    [Fact]
    public void Constructor_InvalidKeyMaterial_NeverLeaksTheKeyMaterialInTheMessage()
    {
        var validHmac = EncryptionTestKeys.Secret("v1:hmac");
        var settings = EncryptionTestKeys.Settings("v1", "v1");
        settings.Keys["v1"] = new EncryptionKeyMaterial { Aes = Convert.ToBase64String(new byte[8]), Hmac = validHmac };

        var act = () => new EncryptionKeyRing(settings);

        var exception = act.Should().Throw<EncryptionConfigurationException>().Which;
        exception.ToString().Should().NotContain(validHmac);
        exception.ToString().Should().NotContain(Convert.ToBase64String(new byte[8]));
    }
}

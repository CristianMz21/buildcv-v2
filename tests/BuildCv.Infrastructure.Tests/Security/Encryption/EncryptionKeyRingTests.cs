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
    public void GetAesKey_ConfiguredKey_ReturnsThirtyTwoBytes()
    {
        var ring = EncryptionTestKeys.SingleKeyRing();

        ring.GetAesKey("v1").ToArray().Should().HaveCount(EncryptionKeyRing.KeySizeInBytes);
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
        settings.Keys["v1"] = settings.Keys["v1"] with { Aes = Convert.ToBase64String(new byte[16]) };

        var act = () => new EncryptionKeyRing(settings);

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:Keys:v1:Aes*")
            .WithMessage("*32 bytes*");
    }

    [Fact]
    public void Constructor_ActiveKeyIdIsNotInKeys_Throws()
    {
        var act = () => new EncryptionKeyRing(EncryptionTestKeys.Settings("v9", "v1"));

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*ActiveKeyId 'v9'*");
    }

    [Fact]
    public void Constructor_ActiveKeyIdIsMissing_Throws()
    {
        var act = () => new EncryptionKeyRing(EncryptionTestKeys.Settings(string.Empty, "v1"));

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*ActiveKeyId must be configured*");
    }

    [Fact]
    public void Constructor_NoKeysConfigured_Throws()
    {
        var act = () => new EncryptionKeyRing(new EncryptionSettings { ActiveKeyId = "v1" });

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*Encryption:Keys must contain at least one key*");
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

    // Key ids reach the Azure Key Vault provider, which flattens nesting onto '--' and reads ':' as
    // a separator. An id carrying either produces an ambiguous secret name
    // (Encryption--Keys--a--b--Aes) and an ambiguous error-message path.
    [Theory]
    [InlineData("clave-año", "only ASCII letters, digits")]
    [InlineData("tenant:v1", "only ASCII letters, digits")]
    [InlineData("tenant_v1", "only ASCII letters, digits")]
    [InlineData("tenant--v1", "consecutive")]
    [InlineData("-v1", "consecutive")]
    [InlineData("v1-", "consecutive")]
    public void Constructor_KeyIdThatWouldCorruptAKeyVaultPath_Throws(string keyId, string expected)
    {
        var act = () => new EncryptionKeyRing(EncryptionTestKeys.Settings(keyId, keyId));

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage($"*{expected}*");
    }

    [Fact]
    public void Constructor_KeyIdLongerThanTheEnvelopeBudget_Throws()
    {
        // The key id is stored on every encrypted row, so it stays short.
        var keyId = new string('v', 33);

        var act = () => new EncryptionKeyRing(EncryptionTestKeys.Settings(keyId, keyId));

        act.Should().Throw<EncryptionConfigurationException>()
            .WithMessage("*exceeds 32 characters*");
    }

    [Fact]
    public void Constructor_KeyIdOfExactlyThirtyTwoCharacters_IsAccepted()
    {
        var keyId = new string('v', 32);

        new EncryptionKeyRing(EncryptionTestKeys.Settings(keyId, keyId)).ActiveKeyId.Should().Be(keyId);
    }

    [Fact]
    public void Settings_AreNeverRenderedWithTheirKeyMaterial()
    {
        // Options objects reach logs, debugger watches and exception dumps; the record-generated
        // ToString would print the secrets verbatim.
        var settings = EncryptionTestKeys.Settings("v1", ["v1", "v2"], "b1", ["b1"]);

        settings.Keys["v1"].ToString().Should().Be("[redacted]");

        var rendered = settings.ToString();
        rendered.Should().NotContain(EncryptionTestKeys.Secret("v1:aes"));
        rendered.Should().NotContain(EncryptionTestKeys.Secret("b1:blind"));
        rendered.Should().Contain("v1").And.Contain("v2").And.Contain("b1");
    }

    [Fact]
    public void Constructor_InvalidKeyMaterial_NeverLeaksTheKeyMaterialInTheMessage()
    {
        var settings = EncryptionTestKeys.Settings("v1", "v1");
        settings.Keys["v1"] = new EncryptionKeyMaterial { Aes = Convert.ToBase64String(new byte[8]) };

        var act = () => new EncryptionKeyRing(settings);

        act.Should().Throw<EncryptionConfigurationException>()
            .Which.ToString().Should().NotContain(Convert.ToBase64String(new byte[8]));
    }
}

using System.Text;
using BuildCv.Infrastructure.Security.Encryption;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Security.Encryption;

public class AesGcmFieldEncryptorTests
{
    private const string Context = "Account.Email";

    private readonly AesGcmFieldEncryptor _encryptor = new(EncryptionTestKeys.SingleKeyRing());

    [Theory]
    [InlineData("candidate@example.com")]
    [InlineData("Añoranza — currículum vitæ 履歴書 🙂")]
    [InlineData("")]
    public void Decrypt_ValueEncryptedUnderTheSameContext_ReturnsTheOriginal(string plaintext)
    {
        var envelope = _encryptor.Encrypt(plaintext, Context);

        _encryptor.Decrypt(envelope, Context).Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_SameInputTwice_ProducesDifferentEnvelopesThatBothDecrypt()
    {
        const string plaintext = "candidate@example.com";

        var first = _encryptor.Encrypt(plaintext, Context);
        var second = _encryptor.Encrypt(plaintext, Context);

        first.Should().NotEqual(second, "a fresh nonce must make identical plaintexts indistinguishable on disk");
        _encryptor.Decrypt(first, Context).Should().Be(plaintext);
        _encryptor.Decrypt(second, Context).Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesTheDocumentedEnvelopeLayout()
    {
        var ring = EncryptionTestKeys.SingleKeyRing("v7");
        var encryptor = new AesGcmFieldEncryptor(ring);
        const string plaintext = "candidate@example.com";
        var plaintextLength = Encoding.UTF8.GetByteCount(plaintext);

        var envelope = encryptor.Encrypt(plaintext, Context);

        envelope[0].Should().Be(AesGcmFieldEncryptor.EnvelopeVersion);
        envelope[1].Should().Be(2, "the key id 'v7' is two ASCII bytes");
        Encoding.ASCII.GetString(envelope, 2, 2).Should().Be("v7");
        envelope.Should().HaveCount(
            AesGcmFieldEncryptor.HeaderSizeInBytes + 2
            + AesGcmFieldEncryptor.NonceSizeInBytes
            + plaintextLength
            + AesGcmFieldEncryptor.TagSizeInBytes);
    }

    [Fact]
    public void Encrypt_NeverEmbedsThePlaintextInTheEnvelope()
    {
        const string plaintext = "candidate@example.com";

        var envelope = _encryptor.Encrypt(plaintext, Context);

        Encoding.UTF8.GetString(envelope).Should().NotContain(plaintext);
    }

    [Fact]
    public void Decrypt_DifferentContext_Throws()
    {
        // The AAD binds a ciphertext to its column: Resume.Summary bytes dropped into Account.Email
        // must fail the tag check, not silently decrypt somewhere they do not belong.
        var envelope = _encryptor.Encrypt("candidate@example.com", "Resume.ContactInformation.Summary");

        var act = () => _encryptor.Decrypt(envelope, Context);

        act.Should().Throw<FieldDecryptionException>()
            .Which.Context.Should().Be(Context);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var envelope = _encryptor.Encrypt("candidate@example.com", Context);
        var ciphertextOffset = AesGcmFieldEncryptor.HeaderSizeInBytes + envelope[1] + AesGcmFieldEncryptor.NonceSizeInBytes;
        envelope[ciphertextOffset] ^= 0x01;

        var act = () => _encryptor.Decrypt(envelope, Context);

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_TamperedAuthenticationTag_Throws()
    {
        var envelope = _encryptor.Encrypt("candidate@example.com", Context);
        envelope[^1] ^= 0x01;

        var act = () => _encryptor.Decrypt(envelope, Context);

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_KeyIdNotInTheRing_ThrowsNamingTheMissingKey()
    {
        var retired = new AesGcmFieldEncryptor(EncryptionTestKeys.SingleKeyRing("retired-v0"));
        var envelope = retired.Encrypt("candidate@example.com", Context);

        var act = () => _encryptor.Decrypt(envelope, Context);

        act.Should().Throw<FieldDecryptionException>()
            .WithMessage("*retired-v0*")
            .WithMessage("*not present in the configured key ring*");
    }

    [Fact]
    public void Decrypt_UnsupportedEnvelopeVersion_Throws()
    {
        var envelope = _encryptor.Encrypt("candidate@example.com", Context);
        envelope[0] = 0x02;

        var act = () => _encryptor.Decrypt(envelope, Context);

        act.Should().Throw<FieldDecryptionException>()
            .WithMessage("*unsupported encryption envelope version 0x02*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public void Decrypt_TruncatedEnvelope_Throws(int length)
    {
        var envelope = _encryptor.Encrypt("candidate@example.com", Context)[..length];

        var act = () => _encryptor.Decrypt(envelope, Context);

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_ZeroLengthKeyId_Throws()
    {
        var envelope = _encryptor.Encrypt("candidate@example.com", Context);
        envelope[1] = 0;

        var act = () => _encryptor.Decrypt(envelope, Context);

        act.Should().Throw<FieldDecryptionException>()
            .WithMessage("*not a well-formed encryption envelope*");
    }

    [Fact]
    public void Decrypt_AfterKeyRotation_StillReadsValuesWrittenUnderTheRetiredKey()
    {
        var beforeRotation = new AesGcmFieldEncryptor(EncryptionTestKeys.Ring("v1", "v1"));
        var legacyEnvelope = beforeRotation.Encrypt("candidate@example.com", Context);

        var afterRotation = new AesGcmFieldEncryptor(EncryptionTestKeys.Ring("v2", "v1", "v2"));

        afterRotation.Decrypt(legacyEnvelope, Context).Should().Be("candidate@example.com");
    }

    [Fact]
    public void Encrypt_AfterKeyRotation_StampsTheNewActiveKeyId()
    {
        var afterRotation = new AesGcmFieldEncryptor(EncryptionTestKeys.Ring("v2", "v1", "v2"));

        var envelope = afterRotation.Encrypt("candidate@example.com", Context);

        Encoding.ASCII.GetString(envelope, AesGcmFieldEncryptor.HeaderSizeInBytes, envelope[1]).Should().Be("v2");
    }

    [Fact]
    public void Decrypt_AfterKeyRotation_CannotReadTheRetiredValueOnceTheKeyLeavesTheRing()
    {
        var beforeRotation = new AesGcmFieldEncryptor(EncryptionTestKeys.Ring("v1", "v1"));
        var legacyEnvelope = beforeRotation.Encrypt("candidate@example.com", Context);

        var withoutLegacyKey = new AesGcmFieldEncryptor(EncryptionTestKeys.Ring("v2", "v2"));

        var act = () => withoutLegacyKey.Decrypt(legacyEnvelope, Context);

        act.Should().Throw<FieldDecryptionException>().WithMessage("*'v1'*");
    }

    [Fact]
    public void Decrypt_Failure_NeverLeaksPlaintextOrKeyMaterialInTheException()
    {
        const string secret = "top-secret-value-that-must-not-leak";
        var envelope = _encryptor.Encrypt(secret, "Resume.ContactInformation.Summary");
        var keyMaterial = EncryptionTestKeys.Secret("v1:aes");

        var act = () => _encryptor.Decrypt(envelope, Context);

        var rendered = act.Should().Throw<FieldDecryptionException>().Which.ToString();
        rendered.Should().NotContain(secret);
        rendered.Should().NotContain(keyMaterial);
        rendered.Should().Contain(Context);
    }

    [Fact]
    public void Encrypt_NullPlaintext_Throws()
    {
        var act = () => _encryptor.Encrypt(null!, Context);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Encrypt_MissingContext_Throws(string? context)
    {
        var act = () => _encryptor.Encrypt("candidate@example.com", context!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decrypt_NullEnvelope_Throws()
    {
        var act = () => _encryptor.Decrypt(null!, Context);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullKeyRing_Throws()
    {
        var act = () => new AesGcmFieldEncryptor(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

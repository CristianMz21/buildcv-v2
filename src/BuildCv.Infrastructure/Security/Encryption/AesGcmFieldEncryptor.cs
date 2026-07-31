using System.Security.Cryptography;
using System.Text;

namespace BuildCv.Infrastructure.Security.Encryption;

// AES-256-GCM with a self-describing envelope:
//
//   [0]                     version, currently 0x01
//   [1]                     key id length in bytes
//   [2 .. 2+n)              key id, ASCII
//   [2+n .. 2+n+12)         nonce
//   [2+n+12 .. len-16)      ciphertext
//   [len-16 .. len)         GCM authentication tag
//
// Encrypt always uses the ring's active key; Decrypt reads the key id out of the envelope, which is
// what makes rotation a configuration change: add the new key, point ActiveKeyId at it, and keep the
// retired key in the ring so existing rows still decrypt without a rewrite.
//
// The additional authenticated data is the UTF-8 property path. It is not stored in the envelope —
// the caller supplies it from the mapping — so a ciphertext copied into a different column fails the
// tag check instead of silently decrypting there.
public sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    internal const byte EnvelopeVersion = 0x01;
    internal const int NonceSizeInBytes = 12;
    internal const int TagSizeInBytes = 16;

    // version byte + key id length byte
    internal const int HeaderSizeInBytes = 2;

    private const int VersionOffset = 0;
    private const int KeyIdLengthOffset = 1;

    private readonly EncryptionKeyRing _keyRing;

    public AesGcmFieldEncryptor(EncryptionKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        _keyRing = keyRing;
    }

    public byte[] Encrypt(string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var keyId = _keyRing.ActiveKeyId;
        var keyIdLength = Encoding.ASCII.GetByteCount(keyId);
        var plaintextLength = Encoding.UTF8.GetByteCount(plaintext);

        var envelope = new byte[HeaderSizeInBytes + keyIdLength + NonceSizeInBytes + plaintextLength + TagSizeInBytes];
        envelope[VersionOffset] = EnvelopeVersion;
        envelope[KeyIdLengthOffset] = (byte)keyIdLength;
        Encoding.ASCII.GetBytes(keyId, envelope.AsSpan(HeaderSizeInBytes, keyIdLength));

        var nonce = envelope.AsSpan(HeaderSizeInBytes + keyIdLength, NonceSizeInBytes);
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = envelope.AsSpan(HeaderSizeInBytes + keyIdLength + NonceSizeInBytes, plaintextLength);
        var tag = envelope.AsSpan(envelope.Length - TagSizeInBytes, TagSizeInBytes);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            using var aesGcm = new AesGcm(_keyRing.GetAesKey(keyId), TagSizeInBytes);
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, Encoding.UTF8.GetBytes(context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        return envelope;
    }

    public string Decrypt(byte[] envelope, string context)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        if (envelope.Length < HeaderSizeInBytes)
            throw Malformed(context);

        if (envelope[VersionOffset] != EnvelopeVersion)
            throw new FieldDecryptionException(
                context,
                $"Stored value for field '{context}' uses unsupported encryption envelope version 0x{envelope[VersionOffset]:X2}.");

        int keyIdLength = envelope[KeyIdLengthOffset];
        var overhead = HeaderSizeInBytes + keyIdLength + NonceSizeInBytes + TagSizeInBytes;
        if (keyIdLength == 0 || envelope.Length < overhead)
            throw Malformed(context);

        var keyId = Encoding.ASCII.GetString(envelope, HeaderSizeInBytes, keyIdLength);
        if (keyId.Any(char.IsControl))
            throw Malformed(context);

        if (!_keyRing.ContainsKey(keyId))
            throw new FieldDecryptionException(
                context,
                $"Stored value for field '{context}' references encryption key '{keyId}', which is not present in the configured key ring.");

        var nonce = envelope.AsSpan(HeaderSizeInBytes + keyIdLength, NonceSizeInBytes);
        var ciphertext = envelope.AsSpan(HeaderSizeInBytes + keyIdLength + NonceSizeInBytes, envelope.Length - overhead);
        var tag = envelope.AsSpan(envelope.Length - TagSizeInBytes, TagSizeInBytes);

        var plaintextBytes = new byte[ciphertext.Length];
        try
        {
            using var aesGcm = new AesGcm(_keyRing.GetAesKey(keyId), TagSizeInBytes);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes, Encoding.UTF8.GetBytes(context));
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (CryptographicException exception)
        {
            // Covers AuthenticationTagMismatchException, which is what a tampered ciphertext or a
            // mismatched context produces. The inner exception carries no plaintext.
            throw new FieldDecryptionException(
                context,
                $"Authentication failed while decrypting field '{context}'. The stored value was tampered with, or it was encrypted under a different property path.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    private static FieldDecryptionException Malformed(string context) =>
        new(context, $"Stored value for field '{context}' is not a well-formed encryption envelope.");
}

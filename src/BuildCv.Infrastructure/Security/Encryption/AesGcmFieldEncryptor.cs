using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
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
// The additional authenticated data is the envelope header (version, key id length, key id) followed
// by the UTF-8 property path. The property path is not stored in the envelope — the caller supplies
// it from the mapping — so a ciphertext copied into a different column fails the tag check instead of
// silently decrypting there. Covering the header too authenticates the version and key id a value was
// sealed under: once a version 0x02 exists, nobody can relabel a 0x02 envelope as 0x01 to force it
// down the weaker path, and no ciphertext can be re-attributed to a different key id.
public sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    internal const byte EnvelopeVersion = 0x01;
    internal const int NonceSizeInBytes = 12;
    internal const int TagSizeInBytes = 16;

    // version byte + key id length byte
    internal const int HeaderSizeInBytes = 2;

    private const int VersionOffset = 0;
    private const int KeyIdLengthOffset = 1;

    // Random 96-bit nonces bind one key to roughly 2^32 encryptions before the birthday bound on
    // nonce collision stops being negligible (NIST SP 800-38D 8.3). At BuildCv's realistic write
    // volume that is on the order of a century — but "realistic volume" is an assumption, not a
    // fact, so it is counted rather than asserted. Operational rule: rotate Encryption:ActiveKeyId
    // annually, and alert if any single key id approaches 2^32.
    //
    // The count below is per PROCESS and resets on every deploy, so it is not the number the budget
    // is about. The alert must fire on the backend-aggregated series — summed across replicas and
    // across process lifetimes, grouped by key_id. Alerting on this local value would never fire.
    private static readonly Meter Meter = new("BuildCv.Infrastructure.Encryption");
    private static readonly ConcurrentDictionary<string, StrongBox<long>> EncryptionCounts = new(StringComparer.Ordinal);

    private readonly EncryptionKeyRing _keyRing;

    static AesGcmFieldEncryptor() =>
        Meter.CreateObservableCounter(
            "buildcv.encryption.operations",
            ObserveEncryptionCounts,
            unit: "{operation}",
            description: "AES-GCM field encryptions performed, per key id. Rotate the key well before 2^32.");

    public AesGcmFieldEncryptor(EncryptionKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        _keyRing = keyRing;
    }

    // Encryptions performed under this ring's active key SINCE THIS PROCESS STARTED. The same number
    // is published through the meter; the budget is evaluated on the aggregated series, not on this.
    public long EncryptionCount =>
        EncryptionCounts.TryGetValue(_keyRing.ActiveKeyId, out var count) ? Interlocked.Read(ref count.Value) : 0;

    public byte[] Encrypt(string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var keyId = _keyRing.ActiveKeyId;
        var keyIdLength = Encoding.ASCII.GetByteCount(keyId);
        var plaintextLength = Encoding.UTF8.GetByteCount(plaintext);
        var headerLength = HeaderSizeInBytes + keyIdLength;

        var envelope = new byte[headerLength + NonceSizeInBytes + plaintextLength + TagSizeInBytes];
        envelope[VersionOffset] = EnvelopeVersion;
        envelope[KeyIdLengthOffset] = (byte)keyIdLength;
        Encoding.ASCII.GetBytes(keyId, envelope.AsSpan(HeaderSizeInBytes, keyIdLength));

        var nonce = envelope.AsSpan(headerLength, NonceSizeInBytes);
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = envelope.AsSpan(headerLength + NonceSizeInBytes, plaintextLength);
        var tag = envelope.AsSpan(envelope.Length - TagSizeInBytes, TagSizeInBytes);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            using var aesGcm = new AesGcm(_keyRing.GetAesKey(keyId), TagSizeInBytes);
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, BuildAad(envelope.AsSpan(0, headerLength), context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        Interlocked.Increment(ref EncryptionCounts.GetOrAdd(keyId, _ => new StrongBox<long>()).Value);

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
        var headerLength = HeaderSizeInBytes + keyIdLength;
        var overhead = headerLength + NonceSizeInBytes + TagSizeInBytes;
        if (keyIdLength == 0 || envelope.Length < overhead)
            throw Malformed(context);

        // Reject the raw bytes before decoding. Encoding.ASCII maps anything >= 0x80 to '?', which
        // would fold distinct key ids together; checking the decoded chars would miss that.
        var keyIdBytes = envelope.AsSpan(HeaderSizeInBytes, keyIdLength);
        foreach (var keyIdByte in keyIdBytes)
        {
            if (keyIdByte is < 0x21 or > 0x7E)
                throw Malformed(context);
        }

        var keyId = Encoding.ASCII.GetString(keyIdBytes);
        if (!_keyRing.ContainsKey(keyId))
            throw new FieldDecryptionException(
                context,
                $"Stored value for field '{context}' references encryption key '{keyId}', which is not present in the configured key ring.");

        var nonce = envelope.AsSpan(headerLength, NonceSizeInBytes);
        var ciphertext = envelope.AsSpan(headerLength + NonceSizeInBytes, envelope.Length - overhead);
        var tag = envelope.AsSpan(envelope.Length - TagSizeInBytes, TagSizeInBytes);

        var plaintextBytes = new byte[ciphertext.Length];
        try
        {
            using var aesGcm = new AesGcm(_keyRing.GetAesKey(keyId), TagSizeInBytes);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes, BuildAad(envelope.AsSpan(0, headerLength), context));
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (CryptographicException exception)
        {
            // Covers AuthenticationTagMismatchException, which is what a tampered ciphertext, a
            // rewritten header or a mismatched context produces. The inner exception carries no
            // plaintext.
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

    private static byte[] BuildAad(ReadOnlySpan<byte> header, string context)
    {
        var contextLength = Encoding.UTF8.GetByteCount(context);
        var aad = new byte[header.Length + contextLength];
        header.CopyTo(aad);
        Encoding.UTF8.GetBytes(context, aad.AsSpan(header.Length));
        return aad;
    }

    private static IEnumerable<Measurement<long>> ObserveEncryptionCounts() =>
        EncryptionCounts.Select(entry => new Measurement<long>(
            Interlocked.Read(ref entry.Value.Value),
            new KeyValuePair<string, object?>("key_id", entry.Key)));

    private static FieldDecryptionException Malformed(string context) =>
        new(context, $"Stored value for field '{context}' is not a well-formed encryption envelope.");
}

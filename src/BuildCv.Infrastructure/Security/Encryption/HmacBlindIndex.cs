using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BuildCv.Infrastructure.Security.Encryption;

// HMAC-SHA256(blind-index key, int32BE(len(context)) || context || value).
//
// The length prefix makes the split between context and value unambiguous by construction, so
// ("Account.Ema", "il@x") and ("Account.Email", "@x") cannot hash to the same digest. An in-band
// separator byte would only hold for as long as no context and no value ever contains that byte.
//
// Inputs are taken verbatim: the Domain already normalizes them (Email lower-cases in its factory),
// and re-normalizing here would let this type and the Domain disagree about what equality means.
//
// Keys come from BlindIndexKeyRing, never from the AES ring: repointing Encryption:ActiveKeyId must
// not change how lookups hash.
public sealed class HmacBlindIndex : IBlindIndex
{
    public const int DigestSizeInBytes = 32;

    private readonly BlindIndexKeyRing _keyRing;

    public HmacBlindIndex(BlindIndexKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        _keyRing = keyRing;
    }

    public byte[] Compute(string value, string context)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        return Compute(value, context, _keyRing.ActiveKeyId);
    }

    public IReadOnlyList<byte[]> ComputeCandidates(string value, string context)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var keyIds = _keyRing.KeyIds;
        var digests = new byte[keyIds.Count][];
        for (var index = 0; index < keyIds.Count; index++)
            digests[index] = Compute(value, context, keyIds[index]);

        return digests;
    }

    private byte[] Compute(string value, string context, string keyId)
    {
        var contextLength = Encoding.UTF8.GetByteCount(context);
        var valueLength = Encoding.UTF8.GetByteCount(value);

        var payload = new byte[sizeof(int) + contextLength + valueLength];
        BinaryPrimitives.WriteInt32BigEndian(payload, contextLength);
        Encoding.UTF8.GetBytes(context, payload.AsSpan(sizeof(int), contextLength));
        Encoding.UTF8.GetBytes(value, payload.AsSpan(sizeof(int) + contextLength, valueLength));

        var digest = new byte[DigestSizeInBytes];
        try
        {
            HMACSHA256.HashData(_keyRing.GetKey(keyId), payload, digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        return digest;
    }
}

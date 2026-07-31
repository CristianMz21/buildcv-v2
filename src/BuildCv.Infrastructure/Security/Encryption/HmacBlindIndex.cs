using System.Security.Cryptography;
using System.Text;

namespace BuildCv.Infrastructure.Security.Encryption;

// HMAC-SHA256(active HMAC key, context + U+001F + value). The unit separator keeps the context and
// the value from running together, so "Account.Ema" + "il@x" and "Account.Email" + "@x" cannot
// collide into the same digest.
//
// Inputs are taken verbatim: the Domain already normalizes them (Email lower-cases in its factory),
// and re-normalizing here would let this type and the Domain disagree about what equality means.
//
// Unlike the encryption envelope the digest carries no key id — it is a fixed 32-byte column. Rotating
// the HMAC secret therefore invalidates every stored index and requires recomputing them; rotate the
// AES secret independently when only payload keys need to move.
public sealed class HmacBlindIndex : IBlindIndex
{
    public const int DigestSizeInBytes = 32;

    // ASCII unit separator: never produced by an email, an opaque token, or a property path.
    private const string ContextSeparator = "\u001F";

    private readonly EncryptionKeyRing _keyRing;

    public HmacBlindIndex(EncryptionKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        _keyRing = keyRing;
    }

    public byte[] Compute(string value, string context)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var payload = Encoding.UTF8.GetBytes(string.Concat(context, ContextSeparator, value));
        var digest = new byte[DigestSizeInBytes];
        try
        {
            HMACSHA256.HashData(_keyRing.GetHmacKey(_keyRing.ActiveKeyId), payload, digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        return digest;
    }
}

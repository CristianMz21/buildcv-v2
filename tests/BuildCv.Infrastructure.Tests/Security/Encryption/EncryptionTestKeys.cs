using System.Security.Cryptography;
using System.Text;
using BuildCv.Infrastructure.Security.Encryption;

namespace BuildCv.Infrastructure.Tests.Security.Encryption;

// Key material is derived from the key id so the same id always yields the same bytes across rings.
// That is what makes the rotation tests meaningful: a ring that adds "v2" keeps "v1" byte-identical.
internal static class EncryptionTestKeys
{
    public static EncryptionSettings Settings(string activeKeyId, params string[] keyIds) => new()
    {
        ActiveKeyId = activeKeyId,
        Keys = keyIds.ToDictionary(keyId => keyId, Material, StringComparer.Ordinal)
    };

    public static EncryptionKeyRing Ring(string activeKeyId, params string[] keyIds) =>
        new(Settings(activeKeyId, keyIds));

    public static EncryptionKeyRing SingleKeyRing(string keyId = "v1") => Ring(keyId, keyId);

    public static EncryptionKeyMaterial Material(string keyId) => new()
    {
        Aes = Secret($"{keyId}:aes"),
        Hmac = Secret($"{keyId}:hmac")
    };

    // SHA-256 is exactly the 32 bytes the key ring demands.
    public static string Secret(string label) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(label)));
}

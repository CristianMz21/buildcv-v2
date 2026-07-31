using System.Security.Cryptography;
using System.Text;
using BuildCv.Infrastructure.Security.Encryption;

namespace BuildCv.Infrastructure.Tests.Security.Encryption;

// Key material is derived from the key id so the same id always yields the same bytes across rings.
// That is what makes the rotation tests meaningful: a ring that adds "v2" keeps "v1" byte-identical,
// and an AES ring rebuilt around a new active id keeps the blind-index secrets untouched.
internal static class EncryptionTestKeys
{
    public static EncryptionSettings Settings(string activeKeyId, params string[] keyIds) =>
        Settings(activeKeyId, keyIds, "b1", ["b1"]);

    public static EncryptionSettings Settings(
        string activeKeyId,
        IEnumerable<string> keyIds,
        string activeBlindIndexKeyId,
        IEnumerable<string> blindIndexKeyIds) => new()
        {
            ActiveKeyId = activeKeyId,
            Keys = keyIds.ToDictionary(keyId => keyId, Material, StringComparer.Ordinal),
            BlindIndex = new BlindIndexSettings
            {
                ActiveKeyId = activeBlindIndexKeyId,
                Keys = blindIndexKeyIds.ToDictionary(
                    keyId => keyId, keyId => Secret($"{keyId}:blind"), StringComparer.Ordinal)
            }
        };

    public static EncryptionKeyRing Ring(string activeKeyId, params string[] keyIds) =>
        new(Settings(activeKeyId, keyIds));

    public static EncryptionKeyRing SingleKeyRing(string keyId = "v1") => Ring(keyId, keyId);

    public static BlindIndexKeyRing BlindIndexRing(string activeKeyId, params string[] keyIds) =>
        new(Settings("v1", ["v1"], activeKeyId, keyIds));

    public static BlindIndexKeyRing SingleBlindIndexRing(string keyId = "b1") => BlindIndexRing(keyId, keyId);

    public static EncryptionKeyMaterial Material(string keyId) => new() { Aes = Secret($"{keyId}:aes") };

    // SHA-256 is exactly the 32 bytes the key rings demand.
    public static string Secret(string label) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(label)));
}

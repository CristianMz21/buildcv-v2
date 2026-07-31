using System.Security.Cryptography;

namespace BuildCv.Infrastructure.Security.Encryption;

// Decodes and validates the configured key material exactly once, at construction, so a malformed
// secret fails at startup rather than on the first row that needs decrypting. Key bytes never leave
// this type as a mutable array: callers get a read-only span they cannot swap out from under it.
public sealed class EncryptionKeyRing
{
    public const int KeySizeInBytes = 32;

    // The envelope stores the key id length in a single byte, so ids cannot exceed 255 bytes.
    private const int MaxKeyIdLength = 255;

    private readonly Dictionary<string, DecodedKey> _keys;

    public EncryptionKeyRing(EncryptionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var error = Validate(settings);
        if (error is not null)
            throw new EncryptionConfigurationException(error);

        _keys = settings.Keys.ToDictionary(
            entry => entry.Key,
            entry => new DecodedKey(
                Convert.FromBase64String(entry.Value.Aes),
                Convert.FromBase64String(entry.Value.Hmac)),
            StringComparer.Ordinal);

        ActiveKeyId = settings.ActiveKeyId;
    }

    // Every new encryption and every blind index uses this key. Retired ids stay resolvable for reads.
    public string ActiveKeyId { get; }

    public bool ContainsKey(string keyId) => _keys.ContainsKey(keyId);

    public ReadOnlySpan<byte> GetAesKey(string keyId) => Resolve(keyId).Aes;

    public ReadOnlySpan<byte> GetHmacKey(string keyId) => Resolve(keyId).Hmac;

    // Returns the first configuration problem found, or null when the settings can build a key ring.
    // Shared with EncryptionSettingsValidator so startup validation and construction cannot drift.
    internal static string? Validate(EncryptionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Keys is null || settings.Keys.Count == 0)
            return $"{EncryptionSettings.SectionName}:Keys must contain at least one key.";

        if (string.IsNullOrWhiteSpace(settings.ActiveKeyId))
            return $"{EncryptionSettings.SectionName}:ActiveKeyId must be configured.";

        foreach (var (keyId, material) in settings.Keys)
        {
            var error = ValidateKey(keyId, material);
            if (error is not null)
                return error;
        }

        return settings.Keys.ContainsKey(settings.ActiveKeyId)
            ? null
            : $"{EncryptionSettings.SectionName}:ActiveKeyId '{settings.ActiveKeyId}' is not present in {EncryptionSettings.SectionName}:Keys.";
    }

    private static string? ValidateKey(string keyId, EncryptionKeyMaterial? material)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            return $"{EncryptionSettings.SectionName}:Keys contains an entry with an empty key id.";

        if (keyId.Length > MaxKeyIdLength)
            return $"{EncryptionSettings.SectionName}:Keys key id '{keyId}' exceeds {MaxKeyIdLength} characters.";

        // The envelope stores key ids as raw ASCII; anything else would round-trip as '?' and
        // silently resolve to the wrong key.
        if (!keyId.All(character => char.IsAscii(character) && !char.IsControl(character)))
            return $"{EncryptionSettings.SectionName}:Keys key id '{keyId}' must use printable ASCII characters only.";

        if (material is null)
            return $"{EncryptionSettings.SectionName}:Keys:{keyId} has no key material.";

        return ValidateMaterial(keyId, nameof(EncryptionKeyMaterial.Aes), material.Aes)
            ?? ValidateMaterial(keyId, nameof(EncryptionKeyMaterial.Hmac), material.Hmac);
    }

    private static string? ValidateMaterial(string keyId, string name, string? value)
    {
        var path = $"{EncryptionSettings.SectionName}:Keys:{keyId}:{name}";

        if (string.IsNullOrWhiteSpace(value))
            return $"{path} must be configured.";

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return $"{path} is not valid base64.";
        }

        var length = decoded.Length;
        CryptographicOperations.ZeroMemory(decoded);

        return length == KeySizeInBytes
            ? null
            : $"{path} must decode to exactly {KeySizeInBytes} bytes but decoded to {length}.";
    }

    private DecodedKey Resolve(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        return _keys.TryGetValue(keyId, out var key)
            ? key
            : throw new EncryptionConfigurationException(
                $"Encryption key '{keyId}' is not present in the configured key ring.");
    }

    private sealed record DecodedKey(byte[] Aes, byte[] Hmac);
}

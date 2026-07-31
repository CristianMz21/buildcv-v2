namespace BuildCv.Infrastructure.Security.Encryption;

// Decodes and validates the configured AES key material exactly once, at construction, so a
// malformed secret fails at startup rather than on the first row that needs decrypting. Key bytes
// never leave this type as a mutable array: callers get a read-only span they cannot swap out from
// under it, and the accessors are internal so only this assembly's crypto can reach them.
//
// This ring covers AES only. Blind-index secrets live in BlindIndexKeyRing so that repointing
// ActiveKeyId here cannot silently change how lookups hash.
public sealed class EncryptionKeyRing
{
    public const int KeySizeInBytes = KeyRingValidation.KeySizeInBytes;

    private const string KeysPath = $"{EncryptionSettings.SectionName}:Keys";

    private readonly Dictionary<string, byte[]> _keys;

    public EncryptionKeyRing(EncryptionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var error = Validate(settings);
        if (error is not null)
            throw new EncryptionConfigurationException(error);

        _keys = settings.Keys.ToDictionary(
            entry => entry.Key,
            entry => Convert.FromBase64String(entry.Value.Aes),
            StringComparer.Ordinal);

        ActiveKeyId = settings.ActiveKeyId;
    }

    // Every new encryption uses this key. Retired ids stay resolvable for reads.
    public string ActiveKeyId { get; }

    public bool ContainsKey(string keyId) => _keys.ContainsKey(keyId);

    internal ReadOnlySpan<byte> GetAesKey(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        return _keys.TryGetValue(keyId, out var key)
            ? key
            : throw new EncryptionConfigurationException(
                $"Encryption key '{keyId}' is not present in the configured key ring.");
    }

    // Returns the first configuration problem found, or null when the settings can build a key ring.
    // Shared with EncryptionSettingsValidator so startup validation and construction cannot drift.
    internal static string? Validate(EncryptionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Keys is null || settings.Keys.Count == 0)
            return $"{KeysPath} must contain at least one key.";

        if (string.IsNullOrWhiteSpace(settings.ActiveKeyId))
            return $"{EncryptionSettings.SectionName}:ActiveKeyId must be configured.";

        foreach (var (keyId, material) in settings.Keys)
        {
            var error = KeyRingValidation.ValidateKeyId(KeysPath, keyId);
            if (error is not null)
                return error;

            if (material is null)
                return $"{KeysPath}:{keyId} has no key material.";

            error = KeyRingValidation.ValidateSecret(
                $"{KeysPath}:{keyId}:{nameof(EncryptionKeyMaterial.Aes)}", material.Aes);
            if (error is not null)
                return error;
        }

        return settings.Keys.ContainsKey(settings.ActiveKeyId)
            ? null
            : $"{EncryptionSettings.SectionName}:ActiveKeyId '{settings.ActiveKeyId}' is not present in {KeysPath}.";
    }
}

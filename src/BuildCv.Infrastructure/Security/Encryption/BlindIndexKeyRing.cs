namespace BuildCv.Infrastructure.Security.Encryption;

// The blind-index secrets, kept in their own ring with their own rotation pointer.
//
// A blind index is a bare 32-byte digest with no room for a key id, so a reader cannot tell which
// key produced a stored value. That is why KeyIds exposes every configured key with the active one
// first: writes use the active key, reads must try them all. Rotation is therefore a four-step
// window — add b2, deploy (writes b2, reads match b2 or b1), backfill, drop b1 — and never a single
// configuration flip.
//
// This type takes BlindIndexSettings, not EncryptionSettings, so the AES rotation pointer is not
// even in scope here. Nothing inside the ring can fall back to it, which is how the pointers stay
// independent structurally rather than by convention.
public sealed class BlindIndexKeyRing
{
    public const int KeySizeInBytes = KeyRingValidation.KeySizeInBytes;

    // Two keys is exactly one rotation in flight. A third means the previous rotation never
    // finished, and every extra live key is another secret that can satisfy a uniqueness lookup —
    // the same duplicate-identity surface the shared-pointer bug opened, just arriving slower.
    public const int MaxKeys = 2;

    private const string SectionPath = $"{EncryptionSettings.SectionName}:BlindIndex";
    private const string KeysPath = $"{SectionPath}:Keys";

    private readonly Dictionary<string, byte[]> _keys;

    public BlindIndexKeyRing(BlindIndexSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var error = Validate(settings);
        if (error is not null)
            throw new EncryptionConfigurationException(error);

        _keys = settings.Keys.ToDictionary(
            entry => entry.Key,
            entry => Convert.FromBase64String(entry.Value),
            StringComparer.Ordinal);

        ActiveKeyId = settings.ActiveKeyId;

        // Active first: a lookup that matches on the first candidate is the common case, and the
        // ordering keeps ComputeCandidates cheap to reason about.
        KeyIds = [ActiveKeyId, .. settings.Keys.Keys.Where(keyId => keyId != ActiveKeyId).Order(StringComparer.Ordinal)];
    }

    // Writes use this key.
    public string ActiveKeyId { get; }

    // Every configured key, active first. Reads must try all of them during a rotation window.
    public IReadOnlyList<string> KeyIds { get; }

    internal ReadOnlySpan<byte> GetKey(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        return _keys.TryGetValue(keyId, out var key)
            ? key
            : throw new EncryptionConfigurationException(
                $"Blind index key '{keyId}' is not present in the configured key ring.");
    }

    // Returns the first configuration problem found, or null when the settings can build a ring.
    internal static string? Validate(BlindIndexSettings? settings)
    {
        if (settings is null || settings.Keys is null || settings.Keys.Count == 0)
            return $"{KeysPath} must contain at least one key.";

        if (string.IsNullOrWhiteSpace(settings.ActiveKeyId))
            return $"{SectionPath}:ActiveKeyId must be configured.";

        // Enforced at startup rather than warned about: an unenforced warning about an unenforced
        // operational step is the same gap one layer down. The deploy after a completed rotation is
        // exactly when this should fail.
        if (settings.Keys.Count > MaxKeys)
            return $"{KeysPath} holds {settings.Keys.Count} keys ({string.Join(", ", settings.Keys.Keys.Order(StringComparer.Ordinal))}) " +
                $"but at most {MaxKeys} may be live at once. Finish the backfill for the retired key and remove it from {KeysPath}.";

        foreach (var (keyId, secret) in settings.Keys)
        {
            var error = KeyRingValidation.ValidateKeyId(KeysPath, keyId)
                ?? KeyRingValidation.ValidateSecret($"{KeysPath}:{keyId}", secret);
            if (error is not null)
                return error;
        }

        return settings.Keys.ContainsKey(settings.ActiveKeyId)
            ? null
            : $"{SectionPath}:ActiveKeyId '{settings.ActiveKeyId}' is not present in {KeysPath}.";
    }
}

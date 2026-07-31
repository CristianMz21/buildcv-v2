namespace BuildCv.Infrastructure.Security.Encryption;

// The blind-index secrets, kept in their own ring with their own rotation pointer.
//
// A blind index is a bare 32-byte digest with no room for a key id, so a reader cannot tell which
// key produced a stored value. That is why KeyIds exposes every configured key with the active one
// first: writes use the active key, reads must try them all. Rotation is therefore a four-step
// window — add b2, deploy (writes b2, reads match b2 or b1), backfill, drop b1 — and never a single
// configuration flip.
public sealed class BlindIndexKeyRing
{
    public const int KeySizeInBytes = KeyRingValidation.KeySizeInBytes;

    private const string SectionPath = $"{EncryptionSettings.SectionName}:BlindIndex";
    private const string KeysPath = $"{SectionPath}:Keys";

    private readonly Dictionary<string, byte[]> _keys;

    public BlindIndexKeyRing(EncryptionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var error = Validate(settings);
        if (error is not null)
            throw new EncryptionConfigurationException(error);

        var blindIndex = settings.BlindIndex;
        _keys = blindIndex.Keys.ToDictionary(
            entry => entry.Key,
            entry => Convert.FromBase64String(entry.Value),
            StringComparer.Ordinal);

        ActiveKeyId = blindIndex.ActiveKeyId;

        // Active first: a lookup that matches on the first candidate is the common case, and the
        // ordering keeps ComputeCandidates cheap to reason about.
        KeyIds = [ActiveKeyId, .. blindIndex.Keys.Keys.Where(keyId => keyId != ActiveKeyId).Order(StringComparer.Ordinal)];
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
    internal static string? Validate(EncryptionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var blindIndex = settings.BlindIndex;
        if (blindIndex is null || blindIndex.Keys is null || blindIndex.Keys.Count == 0)
            return $"{KeysPath} must contain at least one key.";

        if (string.IsNullOrWhiteSpace(blindIndex.ActiveKeyId))
            return $"{SectionPath}:ActiveKeyId must be configured.";

        foreach (var (keyId, secret) in blindIndex.Keys)
        {
            var error = KeyRingValidation.ValidateKeyId(KeysPath, keyId)
                ?? KeyRingValidation.ValidateSecret($"{KeysPath}:{keyId}", secret);
            if (error is not null)
                return error;
        }

        return blindIndex.Keys.ContainsKey(blindIndex.ActiveKeyId)
            ? null
            : $"{SectionPath}:ActiveKeyId '{blindIndex.ActiveKeyId}' is not present in {KeysPath}.";
    }
}

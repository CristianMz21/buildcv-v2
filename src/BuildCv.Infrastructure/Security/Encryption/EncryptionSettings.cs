namespace BuildCv.Infrastructure.Security.Encryption;

// Configuration shape:
//   "Encryption": {
//     "ActiveKeyId": "v1",
//     "Keys": { "v1": { "Aes": "<base64 32 bytes>" } },
//     "BlindIndex": {
//       "ActiveKeyId": "b1",
//       "Keys": { "b1": "<base64 32 bytes>" }
//     }
//   }
//
// Two independent rotation pointers, because the two rotations have opposite blast radius. Retiring
// an AES key id is safe as long as it stays in Keys: ciphertext written under it keeps decrypting
// because the key id travels inside the envelope. A blind-index digest carries no key id, so its
// rotation needs a read-both-write-new window and a backfill.
public sealed record EncryptionSettings
{
    public const string SectionName = "Encryption";

    public string ActiveKeyId { get; init; } = string.Empty;

    public Dictionary<string, EncryptionKeyMaterial> Keys { get; init; } = [];

    public BlindIndexSettings BlindIndex { get; init; } = new();

    // Key ids are safe to surface and are what an operator needs when diagnosing a rotation; the
    // material behind them is not.
    public override string ToString() =>
        $"{nameof(EncryptionSettings)} {{ {nameof(ActiveKeyId)} = {ActiveKeyId}, {nameof(Keys)} = [{string.Join(", ", Keys.Keys)}], {nameof(BlindIndex)} = {BlindIndex} }}";
}

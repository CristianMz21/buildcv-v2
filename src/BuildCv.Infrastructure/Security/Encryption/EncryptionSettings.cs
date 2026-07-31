namespace BuildCv.Infrastructure.Security.Encryption;

// Configuration shape:
//   "Encryption": {
//     "ActiveKeyId": "v1",
//     "Keys": { "v1": { "Aes": "<base64 32 bytes>", "Hmac": "<base64 32 bytes>" } }
//   }
// Retired key ids stay in Keys so ciphertext written under them keeps decrypting after ActiveKeyId
// moves on; only ActiveKeyId decides what new writes are encrypted with.
public sealed record EncryptionSettings
{
    public const string SectionName = "Encryption";

    public string ActiveKeyId { get; init; } = string.Empty;

    public Dictionary<string, EncryptionKeyMaterial> Keys { get; init; } = [];

    // Key ids are safe to surface and are what an operator needs when diagnosing a rotation; the
    // material behind them is not.
    public override string ToString() =>
        $"{nameof(EncryptionSettings)} {{ {nameof(ActiveKeyId)} = {ActiveKeyId}, {nameof(Keys)} = [{string.Join(", ", Keys.Keys)}] }}";
}

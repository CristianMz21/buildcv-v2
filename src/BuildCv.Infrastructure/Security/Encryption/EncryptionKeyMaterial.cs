namespace BuildCv.Infrastructure.Security.Encryption;

// One entry of the key ring. Both secrets are base64 of exactly 32 bytes: Aes feeds AES-256-GCM
// field encryption, Hmac feeds the HMAC-SHA256 blind indexes. They are separate on purpose so an
// index key can never be used to decrypt a payload. The nesting maps 1:1 onto the Azure Key Vault
// configuration provider (Encryption--Keys--v1--Aes), so moving off appsettings needs no code change.
public sealed record EncryptionKeyMaterial
{
    public string Aes { get; init; } = string.Empty;

    public string Hmac { get; init; } = string.Empty;

    // The record-generated ToString would print both secrets, and options objects end up in logs,
    // debugger watches and exception dumps. Same guard the Domain puts on Password.
    public override string ToString() => "[redacted]";
}

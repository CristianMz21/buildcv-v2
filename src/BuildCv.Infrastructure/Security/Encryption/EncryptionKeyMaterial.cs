namespace BuildCv.Infrastructure.Security.Encryption;

// One entry of the AES key ring: base64 of exactly 32 bytes, feeding AES-256-GCM field encryption.
// Maps onto the Azure Key Vault provider as Encryption--Keys--v1--Aes.
//
// Blind-index secrets deliberately do NOT live here. They rotate on their own schedule under
// Encryption:BlindIndex, because rotating an AES key is a safe no-op for existing rows while
// rotating an index key invalidates every stored digest. Sharing one key id between the two let the
// second, far more destructive rotation ride along invisibly with the first.
public sealed record EncryptionKeyMaterial
{
    public string Aes { get; init; } = string.Empty;

    // The record-generated ToString would print the secret, and options objects end up in logs,
    // debugger watches and exception dumps. Same guard the Domain puts on Password.
    public override string ToString() => "[redacted]";
}

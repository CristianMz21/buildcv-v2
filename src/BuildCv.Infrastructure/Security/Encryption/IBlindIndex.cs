namespace BuildCv.Infrastructure.Security.Encryption;

// Deterministic 32-byte lookup token for values that are stored encrypted but still need equality
// search (Account.Email, RefreshToken.Token). The unique index goes on the hash column; never on
// the encrypted column, whose bytes differ on every write.
public interface IBlindIndex
{
    // The digest to WRITE: computed under the active blind-index key.
    byte[] Compute(string value, string context);

    // The digests to MATCH against, one per configured key, active first. A lookup must use this
    // rather than Compute, or a key rotation silently stops matching rows written under the previous
    // key — which reads as "no such account" and lets a duplicate registration through the unique
    // index instead of raising anything.
    IReadOnlyList<byte[]> ComputeCandidates(string value, string context);
}

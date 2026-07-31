namespace BuildCv.Infrastructure.Persistence.Conventions;

// Model annotations this assembly adds on top of EF's own. They exist so the model can be asserted
// against, not just built: ModelConfigurationTests reads them to prove that no encrypted column ever
// ends up in a key or an index, and that a column's encryption context matches where it lives.
internal static class PersistenceAnnotations
{
    // Present and true on every property stored as an AES-GCM envelope.
    public const string Encrypted = "BuildCv:Encrypted";

    // The fully-qualified property path bound into the envelope as additional authenticated data.
    // Mirrors EncryptedConverter<T>.Context so the two cannot drift; a mismatch is a model-build
    // failure in the tests rather than a decryption failure on the first read after deploy.
    public const string EncryptionContext = "BuildCv:EncryptionContext";

    // On a blind-index shadow column: the encryption context of the encrypted column it makes
    // searchable. This is the pairing the guardrail test walks to prove every encrypted column with
    // an exact-match use case has a companion it can actually be looked up by.
    public const string BlindIndexFor = "BuildCv:BlindIndexFor";
}

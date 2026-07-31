namespace BuildCv.Infrastructure.Security.Encryption;

// Single catchable failure type for every way a stored envelope can refuse to decrypt: malformed
// bytes, an unknown envelope version, a key id absent from the ring, a tampered ciphertext, or a
// context that does not match the one the value was sealed under. Messages carry the property path
// and never the plaintext or key material.
public sealed class FieldDecryptionException : Exception
{
    public FieldDecryptionException(string context, string message) : base(message) => Context = context;

    public FieldDecryptionException(string context, string message, Exception innerException)
        : base(message, innerException) => Context = context;

    // Fully-qualified property path the value was sealed under, e.g. "Account.Email".
    public string Context { get; }
}

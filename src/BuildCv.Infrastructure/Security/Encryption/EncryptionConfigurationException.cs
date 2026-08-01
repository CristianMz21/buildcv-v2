namespace BuildCv.Infrastructure.Security.Encryption;

// Raised while building the key ring. Messages name the offending key id and configuration path
// only — never the decoded or encoded key material.
public sealed class EncryptionConfigurationException : Exception
{
    public EncryptionConfigurationException(string message) : base(message) { }

    public EncryptionConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }
}

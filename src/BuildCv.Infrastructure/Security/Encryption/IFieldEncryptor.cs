namespace BuildCv.Infrastructure.Security.Encryption;

// Application-level field encryption. `context` is the fully-qualified property path the value
// belongs to (e.g. "Account.Email") and is bound into the ciphertext as additional authenticated
// data, so an envelope moved to another column fails its authentication tag instead of decrypting.
public interface IFieldEncryptor
{
    byte[] Encrypt(string plaintext, string context);

    string Decrypt(byte[] envelope, string context);
}

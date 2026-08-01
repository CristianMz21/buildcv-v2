using System.Security.Cryptography;

namespace BuildCv.Infrastructure.Security.Encryption;

// Shared rules for both key rings, so the AES ring and the blind-index ring cannot drift on what a
// legal key id or a legal secret looks like.
internal static class KeyRingValidation
{
    public const int KeySizeInBytes = 32;

    // Key ids are stored per row in every encryption envelope, so they stay short. 32 is already
    // generous for "v1"-style identifiers.
    public const int MaxKeyIdLength = 32;

    // Key ids travel through the Azure Key Vault configuration provider, which flattens nesting onto
    // '--' and reads ':' as a separator: an id containing either would produce an ambiguous secret
    // name (Encryption--Keys--a--b--Aes) and an ambiguous error-message path. Leading, trailing and
    // consecutive dashes collide with the same separator, so the charset is deliberately narrow.
    public static string? ValidateKeyId(string path, string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            return $"{path} contains an entry with an empty key id.";

        if (keyId.Length > MaxKeyIdLength)
            return $"{path} key id '{keyId}' exceeds {MaxKeyIdLength} characters.";

        if (!keyId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            return $"{path} key id '{keyId}' must use only ASCII letters, digits and '-'.";

        if (keyId.StartsWith('-') || keyId.EndsWith('-') || keyId.Contains("--", StringComparison.Ordinal))
            return $"{path} key id '{keyId}' must not start, end or contain consecutive '-'; it would collide with the Key Vault '--' separator.";

        return null;
    }

    // Never echoes the value, only the configuration path and the decoded length.
    public static string? ValidateSecret(string path, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{path} must be configured.";

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return $"{path} is not valid base64.";
        }

        var length = decoded.Length;
        CryptographicOperations.ZeroMemory(decoded);

        return length == KeySizeInBytes
            ? null
            : $"{path} must decode to exactly {KeySizeInBytes} bytes but decoded to {length}.";
    }
}

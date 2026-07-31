namespace BuildCv.Domain.Identity;

using System.Diagnostics;
using BuildCv.Domain.Exceptions;

[DebuggerDisplay("Algorithm = {Algorithm}")]
public sealed record Password
{
    private const int MinHashLength = 20;
    private const int MaxHashLength = 256;
    private static readonly string[] AllowedAlgorithms = ["argon2id", "bcrypt", "scrypt", "pbkdf2-hmac-sha256"];

    public string Hash { get; }
    public string Algorithm { get; }

    private Password(string hash, string algorithm)
    {
        Hash = hash;
        Algorithm = algorithm;
    }

    public static Password Create(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        if (hash.Length is < MinHashLength or > MaxHashLength)
            throw new InvalidAccountException("Password hash has invalid length.");

        var algorithm = ParseAlgorithm(hash);
        if (!AllowedAlgorithms.Contains(algorithm))
            throw new InvalidAccountException($"Unsupported password algorithm: '{algorithm}'. Allowed: argon2id, bcrypt, scrypt, pbkdf2-hmac-sha256.");

        return new Password(hash, algorithm);
    }

    private static string ParseAlgorithm(string hash)
    {
        var firstDollar = hash.IndexOf('$');
        var secondDollar = hash.IndexOf('$', firstDollar + 1);
        var segment = secondDollar > 0
            ? hash[(firstDollar + 1)..secondDollar]
            : hash[(firstDollar + 1)..];
        if (segment is "2a" or "2b" or "2y") return "bcrypt";
        return segment;
    }

    public static bool TryCreate(string hash, out Password? password)
    {
        try { password = Create(hash); return true; }
        catch (DomainException) { password = null; return false; }
        catch (ArgumentException) { password = null; return false; }
    }

    public override string ToString() => "[redacted]";
}

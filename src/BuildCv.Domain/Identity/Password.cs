using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Identity;

public sealed record Password
{
    private const int MaxHashLength = 256;
    private const int MaxAlgorithmLength = 20;

    public byte[] Hash { get; }
    public string Algorithm { get; }

    private Password(byte[] hash, string algorithm)
    {
        Hash = hash;
        Algorithm = algorithm;
    }

    public static Password Create(byte[] hash, string algorithm)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);

        if (hash.Length == 0)
            throw new InvalidAccountException("Password hash must not be empty.");

        if (hash.Length > MaxHashLength)
            throw new InvalidAccountException($"Password hash exceeds {MaxHashLength} bytes.");

        if (algorithm.Length > MaxAlgorithmLength)
            throw new InvalidAccountException($"Password algorithm exceeds {MaxAlgorithmLength} characters.");

        return new Password(hash, algorithm);
    }

    public static bool TryCreate(byte[] hash, string algorithm, out Password? password)
    {
        try { password = Create(hash, algorithm); return true; }
        catch (Exception) { password = null; return false; }
    }

    public override string ToString() => $"{Algorithm}:{Convert.ToBase64String(Hash)}";
}

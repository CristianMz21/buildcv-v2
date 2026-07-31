using System.Security.Cryptography;
using System.Text;
using BuildCv.Application.Common.Services;
using Konscious.Security.Cryptography;

namespace BuildCv.Infrastructure.Security;

public sealed class PasswordHasher : IPasswordHasher
{
    private const int MemorySizeKb = 65536;
    private const int Iterations = 3;
    private const int Parallelism = 1;
    private const int HashLengthBytes = 32;
    private const int SaltLengthBytes = 16;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);
        var hash = ComputeHash(password, salt, MemorySizeKb, Iterations, Parallelism);

        return $"$argon2id$v=19$m={MemorySizeKb},t={Iterations},p={Parallelism}" +
            $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string hashedPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        try
        {
            var parts = hashedPassword.Split('$');
            if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id" || parts[2] != "v=19")
                return false;

            if (!TryParseParameters(parts[3], out var memory, out var iterations, out var parallelism))
                return false;

            var salt = Convert.FromBase64String(parts[4]);
            var expectedHash = Convert.FromBase64String(parts[5]);
            if (salt.Length == 0 || expectedHash.Length == 0)
                return false;

            var actualHash = ComputeHash(password, salt, memory, iterations, parallelism, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParseParameters(string segment, out int memory, out int iterations, out int parallelism)
    {
        memory = 0;
        iterations = 0;
        parallelism = 0;

        var parsed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in segment.Split(','))
        {
            var keyValue = pair.Split('=');
            if (keyValue.Length != 2 || !int.TryParse(keyValue[1], out var value) || value <= 0)
                return false;
            parsed[keyValue[0]] = value;
        }

        if (!parsed.TryGetValue("m", out memory) || !parsed.TryGetValue("t", out iterations) ||
            !parsed.TryGetValue("p", out parallelism))
            return false;

        return true;
    }

    private static byte[] ComputeHash(
        string password, byte[] salt, int memorySizeKb, int iterations, int parallelism, int? hashLength = null)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memorySizeKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        return argon2.GetBytes(hashLength ?? HashLengthBytes);
    }
}

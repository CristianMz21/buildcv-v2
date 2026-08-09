namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Services;

public sealed class FakePasswordHasher : IPasswordHasher
{
    /// <summary>
    /// How many times <see cref="Hash"/> has been called.
    /// </summary>
    /// <remarks>
    /// Argon2id is deliberately expensive in the real adapter, so "was this input rejected
    /// BEFORE it bought that work" is a property worth pinning rather than assuming. Without a
    /// counter, a handler that validates after hashing returns the same error and persists the
    /// same nothing, and the assertion cannot tell the two apart.
    /// </remarks>
    public int HashCount { get; private set; }

    public string Hash(string password)
    {
        HashCount++;
        return Compute(password);
    }

    // Verify goes through Compute rather than Hash so that checking an EXISTING credential does
    // not register as hashing a new one. Sharing the counter would make HashCount mean "hash
    // operations" instead of "passwords accepted for hashing", and every ChangePassword
    // assertion about the second would silently be measuring the first.
    public bool Verify(string password, string hashedPassword) => Compute(password) == hashedPassword;

    private static string Compute(string password) => $"$argon2id$fakehash${password}";
}

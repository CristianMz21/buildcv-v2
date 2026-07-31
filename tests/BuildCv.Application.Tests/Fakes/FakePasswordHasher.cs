namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Services;

public sealed class FakePasswordHasher : IPasswordHasher
{
    public string Hash(string password) => $"$argon2id$fakehash${password}";

    public bool Verify(string password, string hashedPassword) => Hash(password) == hashedPassword;
}

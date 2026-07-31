using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Security;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Security;

public class PasswordHasherTests
{
    private const string PlainPassword = "sup3r-secret-password";
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_produces_owasp_parameterized_argon2id_phc_string()
    {
        var hash = _hasher.Hash(PlainPassword);

        hash.Should().StartWith("$argon2id$v=19$m=65536,t=3,p=1$");
    }

    [Fact]
    public void Hash_passes_domain_password_validation()
    {
        var hash = _hasher.Hash(PlainPassword);

        var act = () => Password.Create(hash);

        act.Should().NotThrow();
        Password.Create(hash).Algorithm.Should().Be("argon2id");
    }

    [Fact]
    public void Verify_correct_password_returns_true()
    {
        var hash = _hasher.Hash(PlainPassword);

        _hasher.Verify(PlainPassword, hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_wrong_password_returns_false()
    {
        var hash = _hasher.Hash(PlainPassword);

        _hasher.Verify("different-password", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_same_password_twice_produces_different_hashes()
    {
        var first = _hasher.Hash(PlainPassword);
        var second = _hasher.Hash(PlainPassword);

        first.Should().NotBe(second);
        _hasher.Verify(PlainPassword, first).Should().BeTrue();
        _hasher.Verify(PlainPassword, second).Should().BeTrue();
    }

    [Fact]
    public void Verify_tampered_hash_returns_false_without_throwing()
    {
        var hash = _hasher.Hash(PlainPassword);
        var parts = hash.Split('$');
        var hashSegment = parts[5];
        var tampered = (hashSegment[0] == 'A' ? 'B' : 'A') + hashSegment[1..];
        parts[5] = tampered;

        _hasher.Verify(PlainPassword, string.Join('$', parts)).Should().BeFalse();
    }

    [Fact]
    public void Verify_malformed_hash_returns_false_without_throwing()
    {
        _hasher.Verify(PlainPassword, "not-a-phc-string").Should().BeFalse();
        _hasher.Verify(PlainPassword, "$argon2id$v=19$m=notanumber,t=3,p=1$c2FsdA==$aGFzaA==").Should().BeFalse();
        _hasher.Verify(PlainPassword, "$bcrypt$v=19$m=65536,t=3,p=1$c2FsdA==$aGFzaA==").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Hash_null_or_empty_password_throws(string? password)
    {
        var act = () => _hasher.Hash(password!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_null_or_empty_password_throws(string? password)
    {
        var act = () => _hasher.Verify(password!, _hasher.Hash(PlainPassword));

        act.Should().Throw<ArgumentException>();
    }
}

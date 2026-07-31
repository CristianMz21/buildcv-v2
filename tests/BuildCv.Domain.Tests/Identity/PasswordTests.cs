using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Identity;

public class PasswordTests
{
    private const string Argon2idHash = "$argon2id$v=19$m=65536,t=3,p=1$ saltsalt$somehashoutput";

    [Fact]
    public void Password_from_valid_argon2id_hash_can_be_created()
    {
        var password = Password.Create(Argon2idHash);

        password.Algorithm.Should().Be("argon2id");
        password.Hash.Should().Be(Argon2idHash);
        password.ToString().Should().Be("[redacted]");
    }

    [Fact]
    public void Password_from_bcrypt_variant_maps_to_bcrypt()
    {
        var bcryptHash = "$2b$12$abcdefghijklmnopqrstuvwxyz0123456789abcdefghijkl";

        var password = Password.Create(bcryptHash);

        password.Algorithm.Should().Be("bcrypt");
    }

    [Fact]
    public void Password_rejects_unsupported_algorithm()
    {
        var md5Hash = "$md5$abcdefghijklmnopqrstuvwxyz0123456789ab";

        var act = () => Password.Create(md5Hash);

        act.Should().Throw<InvalidAccountException>();
    }

    [Fact]
    public void Password_rejects_too_short_hash()
    {
        var shortHash = "$argon2id$abc";

        var act = () => Password.Create(shortHash);

        act.Should().Throw<InvalidAccountException>();
    }

    [Fact]
    public void Password_rejects_null_or_empty()
    {
        var actNull = () => Password.Create(null!);
        var actEmpty = () => Password.Create("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryCreate_with_valid_hash_returns_true()
    {
        var result = Password.TryCreate(Argon2idHash, out var password);

        result.Should().BeTrue();
        password.Should().NotBeNull();
    }

    [Fact]
    public void TryCreate_with_unsupported_algorithm_returns_false()
    {
        var result = Password.TryCreate("$md5$abcdefghijklmnopqrstuvwxyz0123456789ab", out var password);

        result.Should().BeFalse();
        password.Should().BeNull();
    }

    [Fact]
    public void TryCreate_with_null_returns_false()
    {
        var result = Password.TryCreate(null!, out var password);

        result.Should().BeFalse();
        password.Should().BeNull();
    }
}

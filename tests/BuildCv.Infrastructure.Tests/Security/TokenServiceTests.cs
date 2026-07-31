using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BuildCv.Infrastructure.Tests.Security;

public class TokenServiceTests
{
    private static readonly JwtSettings Settings = new()
    {
        SigningKey = "0123456789abcdef0123456789abcdef",
        Issuer = "buildcv",
        Audience = "buildcv-api",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30
    };

    private readonly TokenService _service = new(Options.Create(Settings));

    private static Account CreateAccount() =>
        Account.Create(
            Email.Create("user@example.com"),
            Password.Create(new PasswordHasher().Hash("password")),
            Role.Recruiter);

    [Fact]
    public void GenerateAccessToken_contains_sub_email_role_claims()
    {
        var account = CreateAccount();

        var token = _service.GenerateAccessToken(account);

        var parsed = new JsonWebTokenHandler().ReadJsonWebToken(token);
        parsed.Claims.First(c => c.Type == "sub").Value.Should().Be(account.Id.Value.ToString());
        parsed.Claims.First(c => c.Type == "email").Value.Should().Be(account.Email.Value);
        parsed.Claims.First(c => c.Type == "role").Value.Should().Be(Role.Recruiter.ToString());
        parsed.Claims.First(c => c.Type == "jti").Value.Should().NotBeNullOrEmpty();
        parsed.Issuer.Should().Be(Settings.Issuer);
        parsed.Audiences.Should().Contain(Settings.Audience);
    }

    [Fact]
    public void GenerateAccessToken_expires_about_15_minutes_from_now()
    {
        var before = DateTime.UtcNow;

        var token = _service.GenerateAccessToken(CreateAccount());

        var parsed = new JsonWebTokenHandler().ReadJsonWebToken(token);
        parsed.ValidTo.Should().BeAfter(before.AddMinutes(14));
        parsed.ValidTo.Should().BeBefore(DateTime.UtcNow.AddMinutes(16));
    }

    [Fact]
    public void GenerateRefreshToken_produces_unique_tokens_satisfying_domain_minimum_length()
    {
        var first = _service.GenerateRefreshToken();
        var second = _service.GenerateRefreshToken();

        first.Should().NotBe(second);
        first.Length.Should().BeGreaterThanOrEqualTo(43);
        second.Length.Should().BeGreaterThanOrEqualTo(43);
    }

    [Fact]
    public void RefreshTokenLifetime_comes_from_settings() =>
        _service.RefreshTokenLifetime.Should().Be(TimeSpan.FromDays(30));

    [Fact]
    public void Ctor_short_signing_key_throws()
    {
        var shortSettings = new JwtSettings
        {
            SigningKey = "too-short",
            Issuer = "buildcv",
            Audience = "buildcv-api"
        };

        var act = () => new TokenService(Options.Create(shortSettings));

        act.Should().Throw<InvalidOperationException>();
    }
}

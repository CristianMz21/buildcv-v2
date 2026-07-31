using System.Security.Cryptography;
using System.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BuildCv.Infrastructure.Security;

public sealed class TokenService : ITokenService
{
    private readonly JwtSettings _settings;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(IOptions<JwtSettings> options)
    {
        _settings = options.Value;
        if (_settings.SigningKey.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");
    }

    public string GenerateAccessToken(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            Expires = DateTime.UtcNow.AddMinutes(_settings.AccessTokenMinutes),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = account.Id.Value.ToString(),
                ["email"] = account.Email.Value,
                ["role"] = account.Role.ToString(),
                ["jti"] = Guid.NewGuid().ToString()
            }
        };

        return _handler.CreateToken(descriptor);
    }

    public string GenerateRefreshToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_settings.RefreshTokenDays);
}

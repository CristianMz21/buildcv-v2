namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Identity;

public interface ITokenService
{
    string GenerateAccessToken(Account account);
    string GenerateRefreshToken();
    TimeSpan RefreshTokenLifetime { get; }
}

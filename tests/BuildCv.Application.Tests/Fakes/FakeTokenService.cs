namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Identity;

public sealed class FakeTokenService : ITokenService
{
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(30);

    public string GenerateAccessToken(Account account) => $"access-{account.Id.Value}";

    public string GenerateRefreshToken() => $"refresh-token-{Guid.NewGuid():N}";
}

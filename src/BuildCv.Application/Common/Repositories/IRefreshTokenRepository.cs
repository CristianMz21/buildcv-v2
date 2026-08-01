namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Identity;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken cancellationToken = default);
    Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default);
    Task RevokeAsync(string token, CancellationToken cancellationToken = default);

    // Session termination: logout and password change need to kill every token issued to an
    // account, not just the one presented on the current request. The refresh cookie is scoped to
    // /auth/refresh, so /auth/logout never receives it and can only identify the account.
    Task RevokeAllForAccountAsync(AccountId accountId, CancellationToken cancellationToken = default);
}

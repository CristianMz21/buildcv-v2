namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record RefreshAccessTokenCommand(string RefreshToken) : ICommand<Result<AuthResult>>;

public sealed class RefreshAccessTokenHandler(
    IRefreshTokenRepository refreshTokenRepository,
    IAccountRepository accountRepository,
    ITokenService tokenService,
    TimeProvider timeProvider)
    : ICommandHandler<RefreshAccessTokenCommand, Result<AuthResult>>
{
    public async Task<Result<AuthResult>> Handle(RefreshAccessTokenCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await refreshTokenRepository.GetByTokenAsync(command.RefreshToken, cancellationToken);
            if (existing is null)
                return Result<AuthResult>.Failure("Invalid refresh token.");

            if (existing.IsExpired)
                return Result<AuthResult>.Failure("Refresh token has expired.");

            var account = await accountRepository.GetByIdAsync(existing.AccountId, cancellationToken);
            if (account is null || account.Status != AccountStatus.Active)
                return Result<AuthResult>.Failure("Invalid refresh token.");

            await refreshTokenRepository.RevokeAsync(existing.Token, cancellationToken);

            var now = timeProvider.GetUtcNow();
            var accessToken = tokenService.GenerateAccessToken(account);
            var refreshToken = RefreshToken.Create(
                tokenService.GenerateRefreshToken(),
                account.Id,
                now,
                now + tokenService.RefreshTokenLifetime);
            await refreshTokenRepository.AddAsync(refreshToken, cancellationToken);

            return Result<AuthResult>.Success(new AuthResult(account.Id, accessToken, refreshToken));
        }
        catch (DomainException ex)
        {
            return Result<AuthResult>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<AuthResult>.Failure(ex.Message);
        }
    }
}

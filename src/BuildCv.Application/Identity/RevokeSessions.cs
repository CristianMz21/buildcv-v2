namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

/// <summary>
/// Terminates every refresh token issued to an account.
/// </summary>
/// <remarks>
/// Clearing the auth cookies only disarms one browser. The opaque refresh token stays valid in the
/// store for its full lifetime, so a copy captured before logout can be replayed against
/// /auth/refresh to mint fresh access tokens. Revoking server side is what makes logout mean
/// something. Access tokens already handed out stay valid until they expire; that is the standing
/// cost of stateless JWTs, and it bounds the attacker's window to one access-token lifetime.
/// </remarks>
public sealed record RevokeSessionsCommand(AccountId AccountId) : ICommand<Result>;

public sealed class RevokeSessionsHandler(IRefreshTokenRepository refreshTokenRepository)
    : ICommandHandler<RevokeSessionsCommand, Result>
{
    public async Task<Result> Handle(RevokeSessionsCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            await refreshTokenRepository.RevokeAllForAccountAsync(command.AccountId, cancellationToken);
            return Result.Success();
        }
        catch (DomainException ex)
        {
            return Result.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure(ex.Message);
        }
    }
}

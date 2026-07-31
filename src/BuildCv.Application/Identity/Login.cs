namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record LoginCommand(string Email, string Password) : ICommand<Result<AuthResult>>;

public sealed class LoginHandler(
    IAccountRepository accountRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    TimeProvider timeProvider)
    : ICommandHandler<LoginCommand, Result<AuthResult>>
{
    public async Task<Result<AuthResult>> Handle(LoginCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Email.TryCreate(command.Email, out var email) || email is null)
                return Result<AuthResult>.Failure("Invalid credentials.");

            var account = await accountRepository.GetByEmailAsync(email, cancellationToken);
            if (account is null)
                return Result<AuthResult>.Failure("Invalid credentials.");

            if (account.Status != AccountStatus.Active)
                return Result<AuthResult>.Failure("Account is not active.");

            if (account.IsLocked)
                return Result<AuthResult>.Failure("Account is temporarily locked. Try again later.");

            if (!passwordHasher.Verify(command.Password, account.Password.Hash))
            {
                account.RecordFailedLogin();
                await accountRepository.UpdateAsync(account, cancellationToken);
                return Result<AuthResult>.Failure("Invalid credentials.");
            }

            account.RecordSuccessfulLogin();
            await accountRepository.UpdateAsync(account, cancellationToken);

            var authResult = IssueTokens(account);
            await refreshTokenRepository.AddAsync(authResult.RefreshToken, cancellationToken);

            return Result<AuthResult>.Success(authResult);
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

    private AuthResult IssueTokens(Account account)
    {
        var now = timeProvider.GetUtcNow();
        var accessToken = tokenService.GenerateAccessToken(account);
        var refreshToken = RefreshToken.Create(
            tokenService.GenerateRefreshToken(),
            account.Id,
            now,
            now + tokenService.RefreshTokenLifetime);
        return new AuthResult(account.Id, accessToken, refreshToken);
    }
}

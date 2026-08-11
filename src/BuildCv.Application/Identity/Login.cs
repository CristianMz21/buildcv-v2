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

            // AN ACCOUNT WITH NO PASSWORD TAKES THE SAME BRANCH AS A WRONG ONE, and says the same thing.
            // Answering "this account uses Google" would tell any prober which addresses are external,
            // which is a fact about a person's other accounts that this endpoint has no business
            // disclosing to somebody who has proved nothing.
            //
            // The residual is a TIMING difference and it is accepted rather than hidden: Argon2id
            // verification is deliberately slow, so the null path returns measurably sooner. Closing it
            // means verifying against a decoy hash, which is real work to build convincingly and buys
            // less than it looks — the same distinction is available from the sign-up flow, which must
            // tell somebody their address is already registered.
            if (account.Password is null || !passwordHasher.Verify(command.Password, account.Password.Hash))
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

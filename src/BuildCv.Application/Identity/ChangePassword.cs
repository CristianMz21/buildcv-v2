namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record ChangePasswordCommand(AccountId RequesterId, string CurrentPassword, string NewPassword)
    : ICommand<Result<AccountDto>>;

public sealed class ChangePasswordHandler(
    IAccountRepository accountRepository,
    IPasswordHasher passwordHasher,
    IRefreshTokenRepository refreshTokenRepository)
    : ICommandHandler<ChangePasswordCommand, Result<AccountDto>>
{
    /// <summary>Reported when the account has no password to change.</summary>
    public const string NoPasswordError =
        "This account signs in with an external provider and has no password to change.";

    public async Task<Result<AccountDto>> Handle(ChangePasswordCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var account = await accountRepository.GetByIdAsync(command.RequesterId, cancellationToken);
            if (account is null)
                return Result<AccountDto>.Failure("Account not found.");

            // BEFORE the credential check below, for two reasons. Both hashes are Argon2id, so a
            // request that cannot succeed should not buy either one. And a refused NEW password is
            // a validation error, not a wrong guess at the current one — routing it through the
            // lockout path would let a user lock themselves out of their own account by mistyping
            // the password they are trying to choose.
            PasswordPolicy.Validate(command.NewPassword);

            // Verifying the current password makes this a credential check, so it runs through the
            // same lockout path as LoginHandler. Without it the endpoint is a password oracle that
            // accepts unlimited guesses against an already-stolen session.
            if (account.Status != AccountStatus.Active)
                return Result<AccountDto>.Failure("Account is not active.");

            if (account.IsLocked)
                return Result<AccountDto>.Failure("Account is temporarily locked. Try again later.");

            // NAMED PLAINLY HERE, unlike on the login path, and the difference is who is asking: this
            // caller is already authenticated AS this account, so telling them how their own account
            // signs in discloses nothing they could not see on their own settings page. The refusal on
            // login stays generic because that caller has proved nothing.
            //
            // It refuses rather than SETTING a first password. Establishing a credential is not a
            // "change", and doing it from a session alone would let a stolen access token plant a
            // password that outlives the token -- persistent access where today the attacker has
            // fifteen minutes. That path needs its own proof (a fresh provider sign-in) and is not
            // built; see docs/deployment.md.
            if (!account.HasPassword)
                return Result<AccountDto>.Failure(NoPasswordError);

            if (!passwordHasher.Verify(command.CurrentPassword, account.Password!.Hash))
            {
                account.RecordFailedLogin();
                await accountRepository.UpdateAsync(account, cancellationToken);
                return Result<AccountDto>.Failure("Current password is incorrect.");
            }

            account.ChangePassword(Password.Create(passwordHasher.Hash(command.NewPassword)));
            account.ResetLockout();
            await accountRepository.UpdateAsync(account, cancellationToken);

            // Changing a password is the compromise-recovery action users are told to take, so it
            // has to end the sessions an attacker may already hold. Without this the old refresh
            // token keeps minting access tokens for up to 30 days and the rotation buys nothing.
            // Revoking every token for the account (including the caller's own) is deliberate:
            // the refresh cookie is scoped to /auth/refresh, so no handler can tell which token
            // belongs to the caller, and "log out everywhere" is the safe reading of the intent.
            await refreshTokenRepository.RevokeAllForAccountAsync(account.Id, cancellationToken);

            return Result<AccountDto>.Success(AccountDto.From(account));
        }
        catch (DomainException ex)
        {
            return Result<AccountDto>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<AccountDto>.Failure(ex.Message);
        }
    }
}

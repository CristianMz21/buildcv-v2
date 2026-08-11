namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

/// <summary>
/// Redeems a reset token and sets a new password.
/// </summary>
/// <remarks>
/// <para>
/// THE TOKEN IS THE CREDENTIAL, so this endpoint is anonymous and the token has to carry the whole weight.
/// It is signed over the account's CURRENT password hash, which is what makes it single-use with nothing
/// stored: succeeding changes the hash, so the token that was just spent stops verifying, and so does
/// every other token minted before it. A second click on the same link fails, and it fails with the same
/// sentence as a forged one.
/// </para>
/// <para>
/// EVERY SESSION IS REVOKED, exactly as on <c>ChangePassword</c> and for a sharper reason. The person
/// redeeming this may be recovering from a compromise; leaving the attacker's refresh token alive would
/// hand the account back the moment the new password was set.
/// </para>
/// <para>
/// A LOCKOUT DOES NOT BLOCK IT, and that is deliberate. Lockout exists to stop password GUESSING, and this
/// path proves control of the mailbox instead of guessing anything — treating it as blocked would let an
/// attacker lock a victim out of their own recovery by failing logins on purpose. Redeeming clears the
/// lockout, because the legitimate owner has just proven who they are.
/// </para>
/// </remarks>
public sealed record ConfirmPasswordResetCommand(string Token, string NewPassword) : ICommand<Result>;

public sealed class ConfirmPasswordResetHandler(
    IAccountRepository accountRepository,
    IPasswordResetTokenProtector tokenProtector,
    IPasswordHasher passwordHasher,
    IRefreshTokenRepository refreshTokenRepository)
    : ICommandHandler<ConfirmPasswordResetCommand, Result>
{
    // ONE SENTENCE FOR EVERY WAY THE TOKEN CAN FAIL -- forged, expired, already spent, or naming an
    // account that no longer exists. Distinguishing them would turn this endpoint into the enumeration
    // oracle that RequestPasswordReset goes out of its way not to be: "expired" says the account is real.
    public const string InvalidTokenError =
        "This password reset link is invalid or has expired. Request a new one.";

    public async Task<Result> Handle(ConfirmPasswordResetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            // Validated BEFORE the token is looked at, on the same reasoning ChangePassword gives: a new
            // password that breaks policy is a validation error the caller can fix, and it should not
            // consume the one link they were sent.
            PasswordPolicy.Validate(command.NewPassword);

            // A LOOKUP KEY ONLY. Nothing is decided from this: the signature check below is what turns it
            // into a fact, and it is computed against the hash of whatever account this resolved to.
            var accountId = tokenProtector.ReadUnverifiedAccountId(command.Token);
            if (accountId is null)
                return Result.Failure(InvalidTokenError);

            var account = await accountRepository.GetByIdAsync(accountId, cancellationToken);
            if (account is null || account.Status != AccountStatus.Active)
                return Result.Failure(InvalidTokenError);

            // UNREACHABLE THROUGH ANY TOKEN THIS SERVER MINTS, and refused anyway. RequestPasswordReset
            // never issues one for a password-less account, and it could not: the signature is computed
            // OVER the password hash, so there is nothing to sign. This is the belt to that braces --
            // it costs one comparison and it means the invariant survives a future change to how tokens
            // are minted, rather than depending on a second file continuing to behave.
            //
            // It shares InvalidTokenError with every other refusal here on purpose: the caller holds an
            // unverified string, so telling them WHY it failed would let them learn about an account
            // from a token they forged.
            if (account.Password is null)
                return Result.Failure(InvalidTokenError);

            var verified = tokenProtector.Verify(command.Token, account.Id, account.Password.Hash);
            if (!verified.IsSuccess)
                return Result.Failure(InvalidTokenError);

            account.ChangePassword(Password.Create(passwordHasher.Hash(command.NewPassword)));

            // The owner has just proven control of the mailbox, so a lockout accumulated by whoever was
            // guessing at their password has served its purpose and must not outlive the recovery.
            account.ResetLockout();
            await accountRepository.UpdateAsync(account, cancellationToken);

            await refreshTokenRepository.RevokeAllForAccountAsync(account.Id, cancellationToken);

            return Result.Success();
        }
        catch (DomainException ex)
        {
            // PasswordPolicy speaks through this path, and its message is about the password the caller
            // just chose -- not about the token, and not about any account.
            return Result.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure(ex.Message);
        }
    }
}

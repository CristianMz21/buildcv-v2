namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record SignInWithExternalProviderCommand(string Provider, string IdToken)
    : ICommand<Result<AuthResult>>;

/// <summary>
/// Signs somebody in with a token from an external identity provider, creating the account if this is
/// the first time.
/// </summary>
/// <remarks>
/// <para>
/// <b>One refusal message for every failure a caller can cause.</b> An unknown provider, an unconfigured
/// one, a forged signature, an expired token, a wrong audience and an unverified address all answer
/// <see cref="SignInFailedError"/>. Naming which check failed would turn this endpoint into an oracle:
/// "unverified address" says the address exists at Google, and a distinct "no such provider" tells a
/// prober what this server accepts. The server's own logs keep the detail.
/// </para>
/// <para>
/// <b>The account lockout is deliberately NOT consulted here.</b> Lockout exists to stop password
/// guessing, and this path uses no password — so honouring it would let anybody lock a person out of
/// Google sign-in by spamming wrong passwords at their address, which converts a brute-force defence
/// into a denial of service against the very user it protects. Signing in successfully still CLEARS the
/// lockout, which is right: somebody who just proved their identity to Google is not the attacker the
/// counter was raised against.
/// </para>
/// <para>
/// A suspended or deleted account is still refused, because that is a decision this product made about
/// the account rather than a defence against a guesser.
/// </para>
/// </remarks>
public sealed class SignInWithExternalProviderHandler(
    IEnumerable<IExternalIdentityVerifier> verifiers,
    IAccountRepository accountRepository,
    IRefreshTokenRepository refreshTokenRepository,
    ITokenService tokenService,
    TimeProvider timeProvider)
    : ICommandHandler<SignInWithExternalProviderCommand, Result<AuthResult>>
{
    /// <summary>The single answer to every failure the caller can provoke.</summary>
    public const string SignInFailedError = "Could not sign in with that provider.";

    /// <summary>Reported when the account exists but this product has closed it.</summary>
    public const string AccountNotActiveError = "Account is not active.";

    public async Task<Result<AuthResult>> Handle(
        SignInWithExternalProviderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var verifier = verifiers.FirstOrDefault(candidate =>
                string.Equals(candidate.Provider, command.Provider, StringComparison.OrdinalIgnoreCase));

            if (verifier is null || !verifier.IsConfigured)
                return Result<AuthResult>.Failure(SignInFailedError);

            var verification = await verifier.VerifyAsync(command.IdToken, cancellationToken);
            if (!verification.IsSuccess || verification.Value is null)
                return Result<AuthResult>.Failure(SignInFailedError);

            var identity = verification.Value;

            // THE PROVIDER'S OWN CLAIM, AND THE ONE CHECK THAT CANNOT BE SKIPPED. Google issues tokens
            // for addresses it has not proved. Accepting one would create an account stamped
            // EmailVerifiedAt on the strength of nothing, and every later reader -- including a future
            // "verified users only" rule -- would trust that stamp. Worse than having no verification.
            if (!identity.EmailVerified)
                return Result<AuthResult>.Failure(SignInFailedError);

            if (!Email.TryCreate(identity.Email, out var email) || email is null)
                return Result<AuthResult>.Failure(SignInFailedError);

            var account = await accountRepository.GetByEmailAsync(email, cancellationToken);

            if (account is null)
            {
                account = Account.CreateExternal(email);
                account.LinkExternal(verifier.Provider, identity.Subject);
                await accountRepository.AddAsync(account, cancellationToken);
            }
            else
            {
                if (account.Status != AccountStatus.Active)
                    return Result<AuthResult>.Failure(AccountNotActiveError);

                // THE REASSIGNED ADDRESS. Consumer Gmail addresses are never reissued, but Workspace
                // ones are -- alice@corp.com is deleted when Alice leaves and recreated for the next
                // Alice, who arrives with the same address and a NEW subject. Linking on the address
                // alone would hand her the previous Alice's CVs.
                //
                // Refused with the same message as every other failure: telling this caller that the
                // address is known and externally linked is a fact about somebody else's account.
                if (account.IsLinkedToDifferent(verifier.Provider, identity.Subject))
                    return Result<AuthResult>.Failure(SignInFailedError);

                account.LinkExternal(verifier.Provider, identity.Subject);
            }

            // AUTO-LINKING: a provider address that matches an existing password account signs INTO it
            // rather than being refused or duplicated. Safe only because the address was proved by the
            // provider above -- linking on an unverified claim would let anybody who can get a token for
            // your address take over the account you chose a password for.
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

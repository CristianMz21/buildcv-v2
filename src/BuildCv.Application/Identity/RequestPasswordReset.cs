namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

/// <summary>
/// Emails a single-use reset link to an address, if an account is behind it.
/// </summary>
/// <remarks>
/// <para>
/// IT SUCCEEDS WHETHER OR NOT THE ADDRESS EXISTS, and that is the security property rather than sloppiness.
/// An endpoint that answers differently for a known address is an account-enumeration oracle: anyone can
/// walk a list of addresses and learn which people have a CV on this platform, which is itself sensitive —
/// having a CV here means looking for work, and that is a thing somebody's employer might like to know.
/// The caller is told "if that address has an account, a link is on its way" in both cases.
/// </para>
/// <para>
/// THE EXCEPTION IS A MAILER THAT IS NOT WORKING, which is reported. That leaks nothing about accounts —
/// it is the same answer for every address, including ones that do not exist — and the alternative is
/// telling somebody to check an inbox that will never receive anything. See <c>UnconfiguredEmailSender</c>.
/// </para>
/// <para>
/// A non-Active account is treated exactly like a missing one: no mail, and the same answer. Sending a
/// reset link to a deleted account would re-arm a credential for data that has already gone.
/// </para>
/// </remarks>
public sealed record RequestPasswordResetCommand(string Email, string ResetUrlTemplate) : ICommand<Result>;

public sealed class RequestPasswordResetHandler(
    IAccountRepository accountRepository,
    IPasswordResetTokenProtector tokenProtector,
    IEmailSender emailSender)
    : ICommandHandler<RequestPasswordResetCommand, Result>
{
    public const string Subject = "Reset your BuildCv password";

    /// <summary>Reported when the server has no mail provider. Never varies by address.</summary>
    public const string EmailNotConfiguredError = "Password reset by email is not available on this server.";

    /// <summary>Where the token goes in the link the endpoint supplies.</summary>
    public const string TokenPlaceholder = "{token}";

    public async Task<Result> Handle(RequestPasswordResetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            // FIRST, and before the account is looked up at all. This answer is about the server and is
            // the same for every address, so it cannot be used to test whether one is registered.
            // Checking it after the lookup -- which is how this was first written -- made a 503 mean
            // "that address has an account" on any deployment without a mail provider, inverting the
            // whole precaution below.
            if (!emailSender.IsConfigured)
                return Result.Failure(EmailNotConfiguredError);

            // TryCreate rather than Create: a malformed address is a request nobody can act on, and
            // answering success says nothing about which addresses have accounts.
            if (!Email.TryCreate(command.Email, out var email) || email is null)
                return Result.Success();

            var account = await accountRepository.GetByEmailAsync(email, cancellationToken);
            if (account is null || account.Status != AccountStatus.Active)
                return Result.Success();

            var token = tokenProtector.Protect(account.Id, account.Password.Hash);
            var link = command.ResetUrlTemplate.Replace(
                TokenPlaceholder, Uri.EscapeDataString(token), StringComparison.Ordinal);

            var body =
                "Somebody asked to reset the password on your BuildCv account.\n\n"
                + $"{link}\n\n"
                + "The link works once and expires in an hour. If this was not you, nothing has changed "
                + "and you can ignore this message.";

            // A transient send failure is SWALLOWED, unlike the configuration check above, and the
            // asymmetry is deliberate: this branch is reachable only once an account has been found, so
            // reporting it would leak exactly what the equal answers below are protecting. The cost is
            // that a provider outage looks like a delivered mail to one user; the alternative is that it
            // looks like an account register to anybody who asks.
            await emailSender.SendAsync(new EmailMessage(email.Value, Subject, body), cancellationToken);
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

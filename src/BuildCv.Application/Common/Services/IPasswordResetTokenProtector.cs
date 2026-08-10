namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;

/// <summary>
/// Mints and verifies the single-use token that lets somebody who has forgotten their password set a new
/// one.
/// </summary>
/// <remarks>
/// <para>
/// SIGNED, NOT STORED — the same choice <see cref="IImportEvidenceProtector"/> made and for the same
/// reason: this system has no background jobs, so a reset-token table would have no reaper and every
/// abandoned request would leave a permanent row saying that a particular person forgot their password.
/// </para>
/// <para>
/// SINGLE USE COMES FROM THE PASSWORD HASH, which is the whole trick and the reason this needs no storage
/// at all. The signature covers the account's CURRENT password hash, so the moment the token is used the
/// hash changes and every token minted against the old one stops verifying. Changing the password by any
/// other route kills them too. A stored token would have needed a "used" column, a reaper for the ones
/// nobody used, and a race between the two.
/// </para>
/// <para>
/// THE TWO-STEP READ IS DELIBERATE AND IS THE ONE PLACE THIS DIFFERS FROM THE IMPORT PROTECTOR. There, the
/// account came from an authenticated caller, so nothing had to be read from an unverified token. Here the
/// caller is by definition not logged in and the token is the only thing naming an account — so the id has
/// to come out before the signature can be checked, because checking it requires that account's hash.
/// <see cref="ReadUnverifiedAccountId"/> is named the way it is so no call site can pretend otherwise: it
/// is a LOOKUP KEY and nothing else, and an attacker who forges one buys a database read of an account
/// whose hash then fails the signature.
/// </para>
/// </remarks>
public interface IPasswordResetTokenProtector
{
    /// <param name="passwordHash">
    /// The account's current hash. Binding to it is what makes the token single-use; passing anything else
    /// mints a token that can never verify.
    /// </param>
    string Protect(AccountId accountId, string passwordHash);

    /// <summary>
    /// The account id inside the token, WITHOUT verifying anything. Use it to look the account up and for
    /// nothing else — no authorization decision, no response that differs by whether it resolved.
    /// </summary>
    AccountId? ReadUnverifiedAccountId(string token);

    /// <summary>
    /// Verifies the signature against this account and this hash, then the expiry. In that order: nothing
    /// about the token is believed until it is proven to have been minted here.
    /// </summary>
    Result Verify(string token, AccountId accountId, string passwordHash);
}

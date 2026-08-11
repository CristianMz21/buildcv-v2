namespace BuildCv.Api.Contracts;

using BuildCv.Application.Identity;

public sealed record RegisterRequest(string Email, string Password, string? Role);

public sealed record LoginRequest(string Email, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

// DELETE with a body, which is unusual and is the right shape here: the password is a credential, and a
// credential in a query string reaches the access log of every proxy between the client and Kestrel.
/// <param name="CurrentPassword">
/// Required for an account that has one. Ignored, and may be empty, for an account that does not.
/// </param>
/// <param name="ExternalProvider">The provider to re-authenticate against, e.g. <c>"google"</c>.</param>
/// <param name="ExternalIdToken">
/// A <b>fresh</b> identity token, required instead of a password when the account has none.
/// </param>
/// <remarks>
/// <b>The two fields are the same control, not a relaxation of it.</b> Deleting asks for a credential
/// again because an access token is a bearer credential and a stolen one must not be able to erase
/// somebody's employment history. An account created through a provider has no password to re-type, so
/// accepting the session alone — the obvious shortcut — would give exactly that capability to every
/// external account and quietly make them the weakest on the platform. Re-authenticating with the
/// provider is the same "prove it again, now".
/// </remarks>
public sealed record DeleteAccountRequest(
    string CurrentPassword,
    string? ExternalProvider = null,
    string? ExternalIdToken = null);

public sealed record RequestPasswordResetRequest(string Email);

public sealed record ConfirmPasswordResetRequest(string Token, string NewPassword);

public sealed record TokenResponse(string AccessToken, int ExpiresIn);

public sealed record AntiforgeryTokenResponse(string RequestToken);

/// <summary>
/// One account, as returned by <c>POST /v1/auth/register</c>, <c>POST /v1/auth/change-password</c> and
/// <c>GET /v1/auth/me</c>. One shape for one resource, on purpose.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS CHANGED NO BYTE OF THE WIRE.</b> Those three routes answered
/// <c>Application.Identity.AccountDto</c> directly until this type existed — an Application type used as
/// a wire contract, which CLAUDE.md forbids and which every other route stopped doing at v1. It survived
/// <c>V1ContractShapeTests</c> only because <c>AccountDto.From</c> happens to call <c>ToString()</c> on
/// <see cref="Role"/> and <see cref="Status"/>; that sweep fails an enum rendered as a number, not an
/// Application type on the wire, so nothing was checking the rule this breaks.
/// </para>
/// <para>
/// It is separated NOW because the shape is identical now, which makes the move free exactly once. The
/// day a client binds to <c>AccountDto</c>, its property names stop being the Application layer's to
/// choose: renaming a member for the sake of a handler becomes a breaking change to a published
/// contract, and the two concerns can no longer move independently — which is the entire reason the rule
/// exists. <c>AuthContractTests</c> pins the equivalence in both directions: the property list and order
/// were recorded from the real responses BEFORE the swap, and a second test serializes an
/// <c>AccountDto</c> and the <see cref="AccountResponse"/> built from it through the host's own
/// serializer and compares the two strings character for character.
/// </para>
/// <para>
/// <see cref="Role"/> and <see cref="Status"/> are strings that already carry enum NAMES, not enums.
/// Keeping them typed as strings is what makes this a rename-free move; it also means a
/// <c>JsonStringEnumConverter</c> registered globally later cannot change either, the same
/// converter-proofing every other DTO in this folder has.
/// </para>
/// <para>
/// <see cref="LastLoginAt"/> is null on the <c>201</c> from register and only there: registering does
/// not log you in. It is emitted as <c>null</c> rather than omitted, so a client can type it as an
/// always-present nullable.
/// </para>
/// </remarks>
/// <param name="SignInMethods">
/// Every way this account can be signed into — <c>"password"</c> and/or a provider name such as
/// <c>"google"</c>. Always present and never empty.
/// </param>
public sealed record AccountResponse(
    Guid Id,
    string Email,
    string Role,
    string Status,
    bool IsEmailVerified,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> SignInMethods)
{
    /// <param name="account">
    /// The Application layer's view of the account. Mapped from the DTO rather than from
    /// <c>Domain.Identity.Account</c> because that is what the three handlers return, and reaching past
    /// them for the aggregate would give the Api a second way to read one resource.
    /// </param>
    public static AccountResponse From(AccountDto account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new AccountResponse(
            account.Id,
            account.Email,
            account.Role,
            account.Status,
            account.IsEmailVerified,
            account.CreatedAt,
            account.LastLoginAt,
            account.SignInMethods);
    }
}

/// <param name="Provider">The provider that issued the token, e.g. <c>"google"</c>.</param>
/// <param name="IdToken">
/// The provider's identity token, forwarded verbatim and <b>never decoded by the caller</b>.
/// </param>
/// <remarks>
/// <b>The API verifies this signature itself and does not take the caller's word for who somebody is.</b>
/// A body carrying <c>{ email }</c> instead would make whatever can reach this endpoint the authority on
/// identity — and on the deployed topology that is an internal address, not a person. Verification needs
/// the provider's public keys and the expected audience; the client secret belongs only to whatever
/// exchanges an authorisation code, which is not this process.
/// </remarks>
public sealed record ExternalSignInRequest(string Provider, string IdToken);

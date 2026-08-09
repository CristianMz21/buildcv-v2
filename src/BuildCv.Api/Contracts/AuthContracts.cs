namespace BuildCv.Api.Contracts;

using BuildCv.Application.Identity;

public sealed record RegisterRequest(string Email, string Password, string? Role);

public sealed record LoginRequest(string Email, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

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
public sealed record AccountResponse(
    Guid Id,
    string Email,
    string Role,
    string Status,
    bool IsEmailVerified,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt)
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
            account.LastLoginAt);
    }
}

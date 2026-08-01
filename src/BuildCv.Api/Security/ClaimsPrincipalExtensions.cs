using System.Security.Claims;
using BuildCv.Domain.Identity;

namespace BuildCv.Api.Security;

public static class ClaimsPrincipalExtensions
{
    public static AccountId GetAccountId(this ClaimsPrincipal user) =>
        user.GetAccountIdOrNull()
        ?? throw new UnauthorizedAccessException("Authenticated principal is missing a valid sub claim.");

    public static AccountId? GetAccountIdOrNull(this ClaimsPrincipal user)
    {
        var sub = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) && id != Guid.Empty ? new AccountId(id) : null;
    }
}

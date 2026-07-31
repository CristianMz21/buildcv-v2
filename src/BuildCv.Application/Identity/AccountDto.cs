namespace BuildCv.Application.Identity;

using BuildCv.Domain.Identity;

public sealed record AccountDto(
    Guid Id,
    string Email,
    string Role,
    string Status,
    bool IsEmailVerified,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt)
{
    public static AccountDto From(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new AccountDto(
            account.Id.Value,
            account.Email.Value,
            account.Role.ToString(),
            account.Status.ToString(),
            account.IsEmailVerified,
            account.CreatedAt,
            account.LastLoginAt);
    }
}

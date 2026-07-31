namespace BuildCv.Domain.Identity;

using System.Diagnostics;
using BuildCv.Domain.Exceptions;

[DebuggerDisplay("[redacted]")]
public sealed record RefreshToken
{
    private const int MinTokenLength = 43;
    private const int MaxTokenLength = 500;

    public string Token { get; }
    public AccountId AccountId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }

    public bool IsExpired => ExpiresAt < DateTimeOffset.UtcNow;

    private RefreshToken(string token, AccountId accountId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        Token = token;
        AccountId = accountId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public static RefreshToken Create(string token, AccountId accountId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        if (token.Length is < MinTokenLength or > MaxTokenLength)
            throw new InvalidAccountException("Refresh token has invalid length.");

        if (expiresAt <= createdAt)
            throw new InvalidAccountException("Refresh token expiration must be after creation.");

        var maxLifetime = createdAt.AddDays(90);
        if (expiresAt > maxLifetime)
            throw new InvalidAccountException("Refresh token exceeds maximum lifetime of 90 days.");

        return new RefreshToken(token, accountId, createdAt, expiresAt);
    }

    public static bool TryCreate(string token, AccountId accountId, DateTimeOffset createdAt, DateTimeOffset expiresAt, out RefreshToken? refreshToken)
    {
        try { refreshToken = Create(token, accountId, createdAt, expiresAt); return true; }
        catch (DomainException) { refreshToken = null; return false; }
        catch (ArgumentException) { refreshToken = null; return false; }
    }

    public override string ToString() => "[redacted]";
}

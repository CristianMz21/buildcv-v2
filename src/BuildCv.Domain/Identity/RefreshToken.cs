using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Identity;

public sealed record RefreshToken
{
    private const int MaxTokenLength = 500;

    public string Token { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool IsExpired => ExpiresAt < DateTimeOffset.UtcNow;

    private RefreshToken(string token, DateTimeOffset expiresAt)
    {
        Token = token;
        ExpiresAt = expiresAt;
    }

    public static RefreshToken Create(string token, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        if (token.Length > MaxTokenLength)
            throw new InvalidAccountException($"Refresh token exceeds {MaxTokenLength} characters.");

        if (expiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidAccountException("Refresh token expiration must be in the future.");

        return new RefreshToken(token, expiresAt);
    }

    public static bool TryCreate(string token, DateTimeOffset expiresAt, out RefreshToken? refreshToken)
    {
        try { refreshToken = Create(token, expiresAt); return true; }
        catch (Exception) { refreshToken = null; return false; }
    }

    public override string ToString() => Token;
}

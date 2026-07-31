namespace BuildCv.Domain.Identity;

public sealed record AuthResult(
    AccountId AccountId,
    string AccessToken,
    RefreshToken RefreshToken)
{
    public string AccessToken { get; } = !string.IsNullOrWhiteSpace(AccessToken)
        ? AccessToken
        : throw new ArgumentException("AccessToken must not be empty.", nameof(AccessToken));

    public override string ToString() => "[redacted]";
}

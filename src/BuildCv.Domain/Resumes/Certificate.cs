namespace BuildCv.Domain.Resumes;

public sealed record Certificate(
    string Name,
    string Issuer,
    string? CredentialId,
    string? CredentialUrl,
    string? IssuedDate,
    string? ExpirationDate);

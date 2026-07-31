using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Certificate(
    string Name,
    string Issuer,
    string? CredentialId,
    Url? CredentialUrl,
    DateOnly? IssuedDate,
    DateOnly? ExpirationDate);

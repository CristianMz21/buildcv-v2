using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Certificate(
    string Name,
    OrganizationName Issuer,
    string? CredentialId,
    Url? CredentialUrl,
    DateRange? ValidityPeriod);

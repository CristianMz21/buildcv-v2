namespace BuildCv.Api.Contracts;

public sealed record CreateResumeRequest(
    string FullName,
    string Email,
    string? PhoneNumber,
    string? Location,
    string? Summary);

public sealed record UpdateContactRequest(
    string FullName,
    string Email,
    string? PhoneNumber,
    string? Location,
    string? Summary);

public sealed record AddSkillRequest(string SkillName, string? Level, int? YearsOfExperience);

public sealed record AddExperienceRequest(
    string Type,
    string Organization,
    string Position,
    DateOnly Start,
    DateOnly? End,
    string? Summary);

public sealed record AddEducationRequest(
    string Institution,
    string? Degree,
    string? FieldOfStudy,
    DateOnly Start,
    DateOnly? End,
    string? Grade);

public sealed record AddCertificateRequest(
    string Name,
    string Issuer,
    string? CredentialId,
    string? CredentialUrl,
    DateOnly? ValidityStart,
    DateOnly? ValidityEnd);

public sealed record AddProjectRequest(
    string Name,
    DateOnly Start,
    DateOnly? End,
    string? Description,
    string? RepositoryUrl,
    string? LiveDemoUrl,
    string[] Technologies,
    string[] Highlights);

public sealed record AddLanguageRequest(string Name, string? Fluency);

public sealed record AddAwardRequest(string Title, string? Awarder, DateOnly? Date, string? Summary);

public sealed record AddPublicationRequest(
    string Title,
    string? Publisher,
    string? Url,
    DateOnly? ReleaseDate,
    string? Summary);

public sealed record AddInterestRequest(string Name, string[] Keywords);

public sealed record AddReferenceRequest(
    string Name,
    string? Position,
    string? Company,
    string? Email,
    string? PhoneNumber,
    string? ReferenceText);

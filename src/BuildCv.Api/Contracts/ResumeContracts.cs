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

// Level is a string on the wire and an enum inside, parsed in the endpoint. Degree stays free text
// for display and is never parsed into Level — see the comment on Domain.Resumes.Education.Level.
public sealed record AddEducationRequest(
    string Institution,
    string? Degree,
    string? FieldOfStudy,
    DateOnly Start,
    DateOnly? End,
    string? Grade,
    string? Level);

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

// Fluency is free text the candidate types and is shown back verbatim; Level is the comparable value
// the scorer reads. The two are separate fields on purpose — see Domain.Resumes.Language.Level.
public sealed record AddLanguageRequest(string Name, string? Fluency, string? Level);

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

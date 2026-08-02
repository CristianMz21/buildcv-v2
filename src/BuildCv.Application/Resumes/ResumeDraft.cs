namespace BuildCv.Application.Resumes;

// A resume as a HUMAN REVIEW SCREEN holds it, on its way to becoming an aggregate.
//
// EVERY LEAF IS A NULLABLE STRING, INCLUDING THE DATES, THE NUMBERS AND THE ENUMS. That is the whole
// point of the type and it is not a shortcut:
//
//   - The draft carries untrusted text a candidate is correcting — "(555) 123-4567", "2019 - Present",
//     "Avanzado". Typing a field as DateOnly or LanguageProficiency moves the parse into model
//     binding, where a failure is a framework 400 that names no field, collects no siblings and
//     never reaches ResumeDraftValidator. The candidate would be told "the request is invalid" and
//     have to guess which of forty fields caused it.
//   - Because nothing here can fail to bind, the validator always sees the WHOLE draft, which is what
//     lets it report every problem in one pass instead of one per round trip.
//
// The collections are nullable with a null default so an omitted section binds to nothing rather
// than to an empty array the caller never sent; ResumeDraftValidator treats both identically.
public sealed record ResumeDraft(
    ContactDraft? Contact = null,
    IReadOnlyList<ExperienceDraft>? Experiences = null,
    IReadOnlyList<EducationDraft>? Educations = null,
    IReadOnlyList<SkillDraft>? Skills = null,
    IReadOnlyList<ProjectDraft>? Projects = null,
    IReadOnlyList<CertificateDraft>? Certificates = null,
    IReadOnlyList<LanguageDraft>? Languages = null,
    IReadOnlyList<AwardDraft>? Awards = null,
    IReadOnlyList<PublicationDraft>? Publications = null,
    IReadOnlyList<InterestDraft>? Interests = null,
    IReadOnlyList<ReferenceDraft>? References = null);

// Website and Profiles are here because this is the first and only path that can set them. Both are
// mapped, encrypted and round-tripped by ResumeConfiguration, and both were IMPOSSIBLE to populate
// through the API before this file existed: ContactInformationFactory passes a literal null for
// Website, and Profiles has no writer at all.
public sealed record ContactDraft(
    string? FullName = null,
    string? Email = null,
    string? PhoneNumber = null,
    string? Location = null,
    string? Website = null,
    string? Summary = null,
    IReadOnlyList<ProfileDraft>? Profiles = null);

public sealed record ProfileDraft(
    string? Network = null,
    string? Username = null,
    string? Url = null);

public sealed record ExperienceDraft(
    string? Type = null,
    string? Organization = null,
    string? Position = null,
    string? Start = null,
    string? End = null,
    string? Summary = null,
    IReadOnlyList<string?>? Highlights = null);

public sealed record EducationDraft(
    string? Institution = null,
    string? Degree = null,
    string? FieldOfStudy = null,
    string? Start = null,
    string? End = null,
    string? Grade = null,
    string? Level = null);

public sealed record SkillDraft(
    string? Name = null,
    string? Level = null,
    string? YearsOfExperience = null);

public sealed record ProjectDraft(
    string? Name = null,
    string? Start = null,
    string? End = null,
    string? Description = null,
    string? RepositoryUrl = null,
    string? LiveDemoUrl = null,
    IReadOnlyList<string?>? Technologies = null,
    IReadOnlyList<string?>? Highlights = null);

public sealed record CertificateDraft(
    string? Name = null,
    string? Issuer = null,
    string? CredentialId = null,
    string? CredentialUrl = null,
    string? ValidityStart = null,
    string? ValidityEnd = null);

public sealed record LanguageDraft(
    string? Name = null,
    string? Fluency = null,
    string? Level = null);

public sealed record AwardDraft(
    string? Title = null,
    string? Awarder = null,
    string? Date = null,
    string? Summary = null);

public sealed record PublicationDraft(
    string? Title = null,
    string? Publisher = null,
    string? Url = null,
    string? ReleaseDate = null,
    string? Summary = null);

public sealed record InterestDraft(
    string? Name = null,
    IReadOnlyList<string?>? Keywords = null);

public sealed record ReferenceDraft(
    string? Name = null,
    string? Position = null,
    string? Company = null,
    string? Email = null,
    string? PhoneNumber = null,
    string? ReferenceText = null);

/// <summary>
/// One rejected field of a draft. <paramref name="Path"/> is the JSON path of the offending value as
/// the client sent it — <c>experiences[2].end</c>, <c>contact.phoneNumber</c> — so a review screen can
/// highlight the input the candidate has to fix rather than the request as a whole.
/// </summary>
/// <remarks>
/// This is deliberately NOT expressed through <see cref="BuildCv.Domain.Common.ValueObjects.Result{T}"/>:
/// that type carries one string, the API routes on its text, and a draft has forty-plus fields across
/// ten collections. "Invalid phone number." tells a review screen nothing about where to point.
/// </remarks>
public sealed record FieldError(string Path, string Message);

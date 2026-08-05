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
//   - Because no LEAF here is typed, no VALUE can fail to bind: a malformed date, an unknown level or a
//     years count that is not a number all arrive intact and become field errors with a path.
//
// That is the whole of the claim, and it is worth stating what it does not cover. The binder still runs
// first and can still refuse a request before this type exists: a body that is not valid JSON, a lone
// surrogate such as "\ud800" that System.Text.Json rejects while decoding, a bare `null` body, or a body
// over the endpoint's size limit. Those are framework responses with no field path, and they are not
// something a shape can prevent. A `null` ELEMENT inside an array is the case that looks like it belongs
// in that list and does not: it binds fine, so ResumeDraftValidator reports it at its own index.
//
// The collections are nullable with a null default so an omitted section binds to nothing rather
// than to an empty array the caller never sent; ResumeDraftValidator treats both identically.
public sealed record ResumeDraft(
    ContactDraft? Contact = null,
    IReadOnlyList<ExperienceDraft?>? Experiences = null,
    IReadOnlyList<EducationDraft?>? Educations = null,
    IReadOnlyList<SkillDraft?>? Skills = null,
    IReadOnlyList<ProjectDraft?>? Projects = null,
    IReadOnlyList<CertificateDraft?>? Certificates = null,
    IReadOnlyList<LanguageDraft?>? Languages = null,
    IReadOnlyList<AwardDraft?>? Awards = null,
    IReadOnlyList<PublicationDraft?>? Publications = null,
    IReadOnlyList<InterestDraft?>? Interests = null,
    IReadOnlyList<ReferenceDraft?>? References = null);

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
    IReadOnlyList<ProfileDraft?>? Profiles = null);

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

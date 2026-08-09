namespace BuildCv.Api.Contracts;

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;

// The wire shape of a CV, and the last aggregate this API was returning straight out. Every resume
// route answered with the Domain `Resume`, which CLAUDE.md forbids and which made the v1 contract
// false on the two endpoints a candidate actually uses:
//
//   - `id` and `ownerId` shipped as {"value": guid} — not from a DTO decision, but because ResumeId
//     is a strongly-typed record and that is what it serializes into.
//   - EVERY level shipped as a RAW INTEGER: SkillLevel, ExperienceType, EducationLevel and
//     LanguageProficiency. Those numbers are documented in Domain as an append-only PERSISTENCE
//     detail, and the resume graph is where a client meets four of them at once.
//   - Every value object shipped wrapped: {"value": …} for PersonName, Email, PhoneNumber, Url and
//     OrganizationName, {"name": …} for Technology. `Url` was worse than wrapped — it carries a
//     public `Uri` property beside its `Value`, so each URL shipped as an object with the whole
//     parsed System.Uri (scheme, host, segments, port) expanded beside the string.
//
// The DTO states all of it: ids bare, enums by ToString(), value objects flattened to the string they
// wrap. The mapping is declared here field by field so a Domain refactor cannot change the wire by
// accident, and so this file is the one place a wire encoding is decided.
//
// WHAT IS NOT HERE IS ALSO THE CONTRACT. The aggregate exposed nothing a client should not read —
// there are no internal fields to hide — so this is a re-encoding, not a redaction, and the key names
// and nesting are reproduced as they were. Only encodings changed, which is why the round-trip test
// can assert the same field values through the same paths.
public sealed record ResumeResponse(
    Guid Id,
    Guid OwnerId,
    ContactInformationResponse ContactInformation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExperienceResponse> Experiences,
    IReadOnlyList<EducationResponse> Educations,
    IReadOnlyList<SkillResponse> Skills,
    IReadOnlyList<ProjectResponse> Projects,
    IReadOnlyList<CertificateResponse> Certificates,
    IReadOnlyList<LanguageResponse> Languages,
    IReadOnlyList<AwardResponse> Awards,
    IReadOnlyList<PublicationResponse> Publications,
    IReadOnlyList<InterestResponse> Interests,
    IReadOnlyList<ReferenceResponse> References)
{
    public static ResumeResponse From(Resume resume)
    {
        ArgumentNullException.ThrowIfNull(resume);

        return new ResumeResponse(
            resume.Id.Value,
            resume.OwnerId.Value,
            ContactInformationResponse.From(resume.ContactInformation),
            resume.CreatedAt,
            resume.UpdatedAt,
            [.. resume.Experiences.Select(ExperienceResponse.From)],
            [.. resume.Educations.Select(EducationResponse.From)],
            [.. resume.Skills.Select(SkillResponse.From)],
            [.. resume.Projects.Select(ProjectResponse.From)],
            [.. resume.Certificates.Select(CertificateResponse.From)],
            [.. resume.Languages.Select(LanguageResponse.From)],
            [.. resume.Awards.Select(AwardResponse.From)],
            [.. resume.Publications.Select(PublicationResponse.From)],
            [.. resume.Interests.Select(InterestResponse.From)],
            [.. resume.References.Select(ReferenceResponse.From)]);
    }
}

public sealed record ContactInformationResponse(
    string FullName,
    string Email,
    string? PhoneNumber,
    string? Location,
    string? Website,
    string? Summary,
    IReadOnlyList<ProfileResponse> Profiles)
{
    public static ContactInformationResponse From(ContactInformation contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        return new ContactInformationResponse(
            contact.FullName.Value,
            contact.Email.Value,
            contact.PhoneNumber?.Value,
            contact.Location,
            contact.Website?.Value,
            contact.Summary,
            [.. contact.Profiles.Select(ProfileResponse.From)]);
    }
}

public sealed record ProfileResponse(string Network, string? Username, string? Url)
{
    public static ProfileResponse From(Profile profile) =>
        new(profile.Network, profile.Username, profile.Url?.Value);
}

// `type` is the ExperienceType NAME. It is the one level on this aggregate a scoring rule reads
// directly (ComputeExperienceScore counts only Professional entries), so the number behind it is a
// persistence detail a client must never have to know.
public sealed record ExperienceResponse(
    string Type,
    string Organization,
    string Position,
    DateRangeResponse Period,
    string? Summary,
    IReadOnlyList<string> Highlights)
{
    public static ExperienceResponse From(Experience experience) =>
        new(
            experience.Type.ToString(),
            experience.Organization.Value,
            experience.Position,
            DateRangeResponse.From(experience.Period),
            experience.Summary,
            experience.Highlights);
}

// `level` is the EducationLevel name and NULL STAYS NULL: "not stated" and "HighSchool" are different
// claims, HighSchool is 0, and a default here would invent a qualification. Same rule the job
// posting's `educationLevel` follows, for the same reason.
public sealed record EducationResponse(
    string Institution,
    string? Degree,
    string? FieldOfStudy,
    DateRangeResponse Period,
    string? Grade,
    string? Level)
{
    public static EducationResponse From(Education education) =>
        new(
            education.Institution.Value,
            education.Degree,
            education.FieldOfStudy,
            DateRangeResponse.From(education.Period),
            education.Grade,
            education.Level?.ToString());
}

// `name` is the technology as a bare string, matching what POST /v1/resumes/{id}/skills accepts and
// what the import draft carries — the aggregate answered {"name": {"name": "C#"}}, which agreed with
// neither write side.
public sealed record SkillResponse(
    string Name,
    string? Level,
    int? YearsOfExperience,
    IReadOnlyList<string> Keywords)
{
    public static SkillResponse From(Skill skill) =>
        new(skill.Name.Name, skill.Level?.ToString(), skill.YearsOfExperience, skill.Keywords);
}

public sealed record ProjectResponse(
    string Name,
    DateRangeResponse Period,
    string? Description,
    string? RepositoryUrl,
    string? LiveDemoUrl,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Highlights)
{
    public static ProjectResponse From(Project project) =>
        new(
            project.Name,
            DateRangeResponse.From(project.Period),
            project.Description,
            project.RepositoryUrl?.Value,
            project.LiveDemoUrl?.Value,
            [.. project.Technologies.Select(technology => technology.Name)],
            project.Highlights);
}

public sealed record CertificateResponse(
    string Name,
    string Issuer,
    string? CredentialId,
    string? CredentialUrl,
    DateRangeResponse? ValidityPeriod)
{
    public static CertificateResponse From(Certificate certificate) =>
        new(
            certificate.Name,
            certificate.Issuer.Value,
            certificate.CredentialId,
            certificate.CredentialUrl?.Value,
            certificate.ValidityPeriod is { } period ? DateRangeResponse.From(period) : null);
}

// `fluency` and `level` stay two separate fields, and a client must not derive either from the other:
// fluency is free text the candidate wrote about themselves ("Bilingüe", "Nativo (LATAM)") and level
// is the closed enum the scorer compares. See Domain.Resumes.Language.Level for why parsing one into
// the other fails in the direction that hurts the candidate.
public sealed record LanguageResponse(string Name, string? Fluency, string? Level)
{
    public static LanguageResponse From(Language language) =>
        new(language.Name, language.Fluency, language.Level?.ToString());
}

public sealed record AwardResponse(string Title, string? Awarder, DateOnly? Date, string? Summary)
{
    public static AwardResponse From(Award award) =>
        new(award.Title, award.Awarder?.Value, award.Date, award.Summary);
}

public sealed record PublicationResponse(
    string Title,
    string? Publisher,
    string? Url,
    DateOnly? ReleaseDate,
    string? Summary)
{
    public static PublicationResponse From(Publication publication) =>
        new(
            publication.Title,
            publication.Publisher?.Value,
            publication.Url?.Value,
            publication.ReleaseDate,
            publication.Summary);
}

public sealed record InterestResponse(string Name, IReadOnlyList<string> Keywords)
{
    public static InterestResponse From(Interest interest) => new(interest.Name, interest.Keywords);
}

public sealed record ReferenceResponse(
    string Name,
    string? Position,
    string? Company,
    string? Email,
    string? PhoneNumber,
    string? ReferenceText)
{
    public static ReferenceResponse From(Reference reference) =>
        new(
            reference.Name,
            reference.Position,
            reference.Company?.Value,
            reference.Email?.Value,
            reference.PhoneNumber?.Value,
            reference.ReferenceText);
}

// Declared here rather than left to DateRange's own serialization, which already produced exactly
// {start, end} — stating it means a Domain change to that record cannot silently reshape three
// collections and the certificate validity window at once.
//
// STRINGS RATHER THAN DateOnly, because an endpoint carries only as much precision as its source stated
// and a DateOnly cannot express "June 2015" without inventing a day — the same reason the Domain does
// not use one. The JSON is unchanged for every date that already exists: System.Text.Json writes a
// DateOnly as the ISO full date, and PartialDate.ToIsoString writes exactly those ten characters for a
// full-precision value. A partial one is the same field, shorter: "2015-06" or "2015". A client that
// parses this with a strict yyyy-MM-dd reader keeps working for every resume typed by hand and has to
// widen for one imported from a month/year CV, which is the whole point of the change.
public sealed record DateRangeResponse(string Start, string? End)
{
    public static DateRangeResponse From(DateRange period) =>
        new(period.Start.ToIsoString(), period.End?.ToIsoString());
}

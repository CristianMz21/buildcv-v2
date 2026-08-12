namespace BuildCv.Api.Contracts;

using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
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
    string? Name,
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
    /// <param name="loaded">
    /// The resume and the identity of its entries, from ONE load. Taken as a pair rather than as two
    /// arguments because <see cref="ResumeItemIds"/>' positional alignment is only true within a single
    /// materialization — two arguments would let a caller pair ids with a different read and hand a
    /// candidate an id that names somebody else's bullet point.
    /// </param>
    public static ResumeResponse From(ResumeWithItemIds loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        var resume = loaded.Resume;
        var ids = loaded.ItemIds;

        return new ResumeResponse(
            resume.Id.Value,
            resume.OwnerId.Value,
            resume.Name,
            ContactInformationResponse.From(resume.ContactInformation),
            resume.CreatedAt,
            resume.UpdatedAt,
            ItemIdProjection.Project("Resume", resume.Experiences, ids.For(ResumeSection.Experiences), ExperienceResponse.From),
            ItemIdProjection.Project("Resume", resume.Educations, ids.For(ResumeSection.Educations), EducationResponse.From),
            ItemIdProjection.Project("Resume", resume.Skills, ids.For(ResumeSection.Skills), SkillResponse.From),
            ItemIdProjection.Project("Resume", resume.Projects, ids.For(ResumeSection.Projects), ProjectResponse.From),
            ItemIdProjection.Project("Resume", resume.Certificates, ids.For(ResumeSection.Certificates), CertificateResponse.From),
            ItemIdProjection.Project("Resume", resume.Languages, ids.For(ResumeSection.Languages), LanguageResponse.From),
            ItemIdProjection.Project("Resume", resume.Awards, ids.For(ResumeSection.Awards), AwardResponse.From),
            ItemIdProjection.Project("Resume", resume.Publications, ids.For(ResumeSection.Publications), PublicationResponse.From),
            ItemIdProjection.Project("Resume", resume.Interests, ids.For(ResumeSection.Interests), InterestResponse.From),
            ItemIdProjection.Project("Resume", resume.References, ids.For(ResumeSection.References), ReferenceResponse.From));
    }
}

/// <summary>
/// One resume as it appears in a LIST, as returned by <c>GET /v1/resumes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It carries no collections, only their sizes.</b> That route used to answer with the whole
/// aggregate — every experience, skill, certificate and reference of every resume on the page — which
/// made a twenty-row page of a fully populated CV thousands of rows off the wire to render a picker
/// showing a name and a date. Nothing that reads a list needs the contents.
/// </para>
/// <para>
/// It is also what makes the per-entry <c>id</c> on <see cref="ResumeResponse"/> honest. Those ids come
/// from a TRACKED load; the list query is deliberately untracked, so a list that still carried the
/// collections would have to answer either with no ids or with ids meaning something different from the
/// ones on <c>GET /v1/resumes/{id}</c>. A field that means two things depending on the route is worse
/// than a field that is absent, so the collections leave instead.
/// </para>
/// <para>
/// The change shipped while the only client of v1 was this repository's own web app — the same window,
/// and the same argument, that let <c>AnalysisResponse</c> unwrap its ids and name its enums in the
/// release that introduced <c>/v1</c>. It closes the moment anything else binds.
/// </para>
/// </remarks>
public sealed record ResumeSummaryResponse(
    Guid Id,
    Guid OwnerId,
    /// <summary>
    /// What the candidate calls this CV, or null when they have not named one. It is the ONLY field
    /// on this row that differs between two CVs of one person — the contact name is identical across
    /// them by definition — so a picker leads with this and falls back to the contact name.
    /// </summary>
    string? Name,
    string FullName,
    string Email,
    string? Location,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ResumeSectionCounts Counts)
{
    public static ResumeSummaryResponse From(Resume resume)
    {
        ArgumentNullException.ThrowIfNull(resume);

        return new ResumeSummaryResponse(
            resume.Id.Value,
            resume.OwnerId.Value,
            resume.Name,
            resume.ContactInformation.FullName.Value,
            resume.ContactInformation.Email.Value,
            resume.ContactInformation.Location,
            resume.CreatedAt,
            resume.UpdatedAt,
            ResumeSectionCounts.From(resume));
    }
}

/// <summary>
/// How many entries each section holds. Counts rather than contents: a picker shows "8 skills, 3 roles"
/// and an empty-state decides on zero, and neither needs the entries themselves.
/// </summary>
public sealed record ResumeSectionCounts(
    int Experiences,
    int Educations,
    int Skills,
    int Projects,
    int Certificates,
    int Languages,
    int Awards,
    int Publications,
    int Interests,
    int References)
{
    public static ResumeSectionCounts From(Resume resume) =>
        new(
            resume.Experiences.Count,
            resume.Educations.Count,
            resume.Skills.Count,
            resume.Projects.Count,
            resume.Certificates.Count,
            resume.Languages.Count,
            resume.Awards.Count,
            resume.Publications.Count,
            resume.Interests.Count,
            resume.References.Count);

    public static ResumeSectionCounts From(CandidateProfile profile) =>
        new(
            profile.Experiences.Count,
            profile.Educations.Count,
            profile.Skills.Count,
            profile.Projects.Count,
            profile.Certificates.Count,
            profile.Languages.Count,
            profile.Awards.Count,
            profile.Publications.Count,
            profile.Interests.Count,
            profile.References.Count);
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
    int Id,
    string Type,
    string Organization,
    string Position,
    DateRangeResponse Period,
    string? Summary,
    IReadOnlyList<string> Highlights)
{
    public static ExperienceResponse From(int id, Experience experience) =>
        new(
            id,
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
    int Id,
    string Institution,
    string? Degree,
    string? FieldOfStudy,
    DateRangeResponse Period,
    string? Grade,
    string? Level)
{
    public static EducationResponse From(int id, Education education) =>
        new(
            id,
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
    int Id,
    string Name,
    string? Level,
    int? YearsOfExperience,
    IReadOnlyList<string> Keywords)
{
    public static SkillResponse From(int id, Skill skill) =>
        new(id, skill.Name.Name, skill.Level?.ToString(), skill.YearsOfExperience, skill.Keywords);
}

public sealed record ProjectResponse(
    int Id,
    string Name,
    DateRangeResponse Period,
    string? Description,
    string? RepositoryUrl,
    string? LiveDemoUrl,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Highlights)
{
    public static ProjectResponse From(int id, Project project) =>
        new(
            id,
            project.Name,
            DateRangeResponse.From(project.Period),
            project.Description,
            project.RepositoryUrl?.Value,
            project.LiveDemoUrl?.Value,
            [.. project.Technologies.Select(technology => technology.Name)],
            project.Highlights);
}

public sealed record CertificateResponse(
    int Id,
    string Name,
    string Issuer,
    string? CredentialId,
    string? CredentialUrl,
    DateRangeResponse? ValidityPeriod)
{
    public static CertificateResponse From(int id, Certificate certificate) =>
        new(
            id,
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
public sealed record LanguageResponse(int Id, string Name, string? Fluency, string? Level)
{
    public static LanguageResponse From(int id, Language language) =>
        new(id, language.Name, language.Fluency, language.Level?.ToString());
}

public sealed record AwardResponse(
    int Id, string Title, string? Awarder, DateOnly? Date, string? Summary)
{
    public static AwardResponse From(int id, Award award) =>
        new(id, award.Title, award.Awarder?.Value, award.Date, award.Summary);
}

public sealed record PublicationResponse(
    int Id,
    string Title,
    string? Publisher,
    string? Url,
    DateOnly? ReleaseDate,
    string? Summary)
{
    public static PublicationResponse From(int id, Publication publication) =>
        new(
            id,
            publication.Title,
            publication.Publisher?.Value,
            publication.Url?.Value,
            publication.ReleaseDate,
            publication.Summary);
}

public sealed record InterestResponse(int Id, string Name, IReadOnlyList<string> Keywords)
{
    public static InterestResponse From(int id, Interest interest) =>
        new(id, interest.Name, interest.Keywords);
}

public sealed record ReferenceResponse(
    int Id,
    string Name,
    string? Position,
    string? Company,
    string? Email,
    string? PhoneNumber,
    string? ReferenceText)
{
    public static ReferenceResponse From(int id, Reference reference) =>
        new(
            id,
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

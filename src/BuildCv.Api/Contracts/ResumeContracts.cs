namespace BuildCv.Api.Contracts;

using BuildCv.Application.Resumes;

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

// The wire shape of POST /resumes/import — one whole CV, as the review screen the candidate corrects
// their extracted CV on submits it.
//
// EVERY LEAF IS AN OPTIONAL STRING, dates and numbers and levels included. See the comment on
// Application.Resumes.ResumeDraft for why: a typed field would fail at model binding, which produces a
// framework 400 naming no field and collecting no siblings, and the candidate would never reach the
// validator that can tell them all five things that are wrong at once.
//
// It mirrors ResumeDraft field for field rather than being it, because CLAUDE.md forbids an Application
// type on the wire. The duplication buys the freedom to rename a draft field without breaking clients;
// what it costs is that a swapped mapping below is a real bug, which is why the round-trip tests assert
// a DISTINCT value per field instead of a plausible-looking one.
//
// EVERY ELEMENT IS NULLABLE, and that is not decoration. `{"skills":[null]}` is valid JSON, and
// System.Text.Json does not enforce nullable reference annotations — it binds a one-element list holding
// null whatever the declared type says. Calling `item.ToDraft()` on that was an unhandled
// NullReferenceException, i.e. a 500, on all eleven collections plus contact.profiles. The nulls are
// mapped through as nulls and ResumeDraftValidator.ForEachCapped reports each one as a field error at
// its own index, which is what the string arrays already did.
//
// THE MAPPING IS LAZY, via ProjectedList. `.Select(...).ToList()` here doubled the object graph before
// the validator had even read Count to find the section over its cap; see ProjectedList for the
// arithmetic.
public sealed record ImportResumeRequest(
    ImportContactRequest? Contact = null,
    IReadOnlyList<ImportExperienceRequest?>? Experiences = null,
    IReadOnlyList<ImportEducationRequest?>? Educations = null,
    IReadOnlyList<ImportSkillRequest?>? Skills = null,
    IReadOnlyList<ImportProjectRequest?>? Projects = null,
    IReadOnlyList<ImportCertificateRequest?>? Certificates = null,
    IReadOnlyList<ImportLanguageRequest?>? Languages = null,
    IReadOnlyList<ImportAwardRequest?>? Awards = null,
    IReadOnlyList<ImportPublicationRequest?>? Publications = null,
    IReadOnlyList<ImportInterestRequest?>? Interests = null,
    IReadOnlyList<ImportReferenceRequest?>? References = null)
{
    public ResumeDraft ToDraft() => new(
        Contact?.ToDraft(),
        ProjectedList.OrNull(Experiences, item => item?.ToDraft()),
        ProjectedList.OrNull(Educations, item => item?.ToDraft()),
        ProjectedList.OrNull(Skills, item => item?.ToDraft()),
        ProjectedList.OrNull(Projects, item => item?.ToDraft()),
        ProjectedList.OrNull(Certificates, item => item?.ToDraft()),
        ProjectedList.OrNull(Languages, item => item?.ToDraft()),
        ProjectedList.OrNull(Awards, item => item?.ToDraft()),
        ProjectedList.OrNull(Publications, item => item?.ToDraft()),
        ProjectedList.OrNull(Interests, item => item?.ToDraft()),
        ProjectedList.OrNull(References, item => item?.ToDraft()));
}

// Website and Profiles are reachable for the first time here. Both are mapped and encrypted by
// ResumeConfiguration and neither could be set through any existing route: CreateResume's
// ContactInformationFactory hardcodes a null Website, and Profiles had no writer at all.
public sealed record ImportContactRequest(
    string? FullName = null,
    string? Email = null,
    string? PhoneNumber = null,
    string? Location = null,
    string? Website = null,
    string? Summary = null,
    IReadOnlyList<ImportProfileRequest?>? Profiles = null)
{
    public ContactDraft ToDraft() => new(
        FullName, Email, PhoneNumber, Location, Website, Summary,
        ProjectedList.OrNull(Profiles, item => item?.ToDraft()));
}

public sealed record ImportProfileRequest(
    string? Network = null,
    string? Username = null,
    string? Url = null)
{
    public ProfileDraft ToDraft() => new(Network, Username, Url);
}

public sealed record ImportExperienceRequest(
    string? Type = null,
    string? Organization = null,
    string? Position = null,
    string? Start = null,
    string? End = null,
    string? Summary = null,
    IReadOnlyList<string?>? Highlights = null)
{
    public ExperienceDraft ToDraft() => new(Type, Organization, Position, Start, End, Summary, Highlights);
}

public sealed record ImportEducationRequest(
    string? Institution = null,
    string? Degree = null,
    string? FieldOfStudy = null,
    string? Start = null,
    string? End = null,
    string? Grade = null,
    string? Level = null)
{
    public EducationDraft ToDraft() => new(Institution, Degree, FieldOfStudy, Start, End, Grade, Level);
}

public sealed record ImportSkillRequest(
    string? Name = null,
    string? Level = null,
    string? YearsOfExperience = null)
{
    public SkillDraft ToDraft() => new(Name, Level, YearsOfExperience);
}

public sealed record ImportProjectRequest(
    string? Name = null,
    string? Start = null,
    string? End = null,
    string? Description = null,
    string? RepositoryUrl = null,
    string? LiveDemoUrl = null,
    IReadOnlyList<string?>? Technologies = null,
    IReadOnlyList<string?>? Highlights = null)
{
    public ProjectDraft ToDraft() =>
        new(Name, Start, End, Description, RepositoryUrl, LiveDemoUrl, Technologies, Highlights);
}

public sealed record ImportCertificateRequest(
    string? Name = null,
    string? Issuer = null,
    string? CredentialId = null,
    string? CredentialUrl = null,
    string? ValidityStart = null,
    string? ValidityEnd = null)
{
    public CertificateDraft ToDraft() => new(Name, Issuer, CredentialId, CredentialUrl, ValidityStart, ValidityEnd);
}

public sealed record ImportLanguageRequest(
    string? Name = null,
    string? Fluency = null,
    string? Level = null)
{
    public LanguageDraft ToDraft() => new(Name, Fluency, Level);
}

public sealed record ImportAwardRequest(
    string? Title = null,
    string? Awarder = null,
    string? Date = null,
    string? Summary = null)
{
    public AwardDraft ToDraft() => new(Title, Awarder, Date, Summary);
}

public sealed record ImportPublicationRequest(
    string? Title = null,
    string? Publisher = null,
    string? Url = null,
    string? ReleaseDate = null,
    string? Summary = null)
{
    public PublicationDraft ToDraft() => new(Title, Publisher, Url, ReleaseDate, Summary);
}

public sealed record ImportInterestRequest(
    string? Name = null,
    IReadOnlyList<string?>? Keywords = null)
{
    public InterestDraft ToDraft() => new(Name, Keywords);
}

public sealed record ImportReferenceRequest(
    string? Name = null,
    string? Position = null,
    string? Company = null,
    string? Email = null,
    string? PhoneNumber = null,
    string? ReferenceText = null)
{
    public ReferenceDraft ToDraft() => new(Name, Position, Company, Email, PhoneNumber, ReferenceText);
}

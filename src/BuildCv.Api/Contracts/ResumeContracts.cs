namespace BuildCv.Api.Contracts;

using BuildCv.Application.Resumes;

public sealed record CreateResumeRequest(
    string FullName,
    string Email,
    string? PhoneNumber,
    string? Location,
    string? Summary);

/// <summary>
/// Names a CV. A null or blank <paramref name="Name"/> CLEARS the name rather than storing an empty
/// one — "not named" and "named nothing" are the same state to a candidate.
/// </summary>
public sealed record RenameResumeRequest(string? Name);

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
    IReadOnlyList<ImportReferenceRequest?>? References = null,
    string? ImportEvidence = null)
{
    // IT IS NOT PART OF ToDraft, and that is the point of it being last. ResumeDraft is "the resume as a
    // review screen holds it" — every leaf a nullable string the candidate may correct — and this is the
    // one field they must NOT correct: it is an opaque token the server signed, and a hand-edited one is
    // a rejected one. It rides on this record because it is a field of the same request body, and it
    // travels to the handler beside the draft rather than inside it.

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

    // The OTHER direction: a parsed draft mapped INTO this wire shape so the propose response hands the
    // review screen exactly what it will post back to POST /resumes/import after the candidate corrects
    // it. Confidence is NOT here — it travels in a separate structure and never crosses back.
    public static ImportResumeRequest FromDraft(ResumeDraft draft) => new(
        draft.Contact is null ? null : ImportContactRequest.FromDraft(draft.Contact),
        DraftMapping.Map(draft.Experiences, ImportExperienceRequest.FromDraft),
        DraftMapping.Map(draft.Educations, ImportEducationRequest.FromDraft),
        DraftMapping.Map(draft.Skills, ImportSkillRequest.FromDraft),
        DraftMapping.Map(draft.Projects, ImportProjectRequest.FromDraft),
        DraftMapping.Map(draft.Certificates, ImportCertificateRequest.FromDraft),
        DraftMapping.Map(draft.Languages, ImportLanguageRequest.FromDraft),
        DraftMapping.Map(draft.Awards, ImportAwardRequest.FromDraft),
        DraftMapping.Map(draft.Publications, ImportPublicationRequest.FromDraft),
        DraftMapping.Map(draft.Interests, ImportInterestRequest.FromDraft),
        DraftMapping.Map(draft.References, ImportReferenceRequest.FromDraft));
}

// Maps a draft collection into its wire-contract collection, preserving null elements (a hostile null
// element survives the round trip and is reported at its index by ResumeDraftValidator on submit, exactly
// as it is on the request path).
internal static class DraftMapping
{
    public static IReadOnlyList<TRequest?>? Map<TDraft, TRequest>(
        IReadOnlyList<TDraft?>? items, Func<TDraft, TRequest> map)
        where TDraft : class
        where TRequest : class =>
        items?.Select(item => item is null ? null : map(item)).ToList();
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

    public static ImportContactRequest FromDraft(ContactDraft draft) => new(
        draft.FullName, draft.Email, draft.PhoneNumber, draft.Location, draft.Website, draft.Summary,
        DraftMapping.Map(draft.Profiles, ImportProfileRequest.FromDraft));
}

public sealed record ImportProfileRequest(
    string? Network = null,
    string? Username = null,
    string? Url = null)
{
    public ProfileDraft ToDraft() => new(Network, Username, Url);

    public static ImportProfileRequest FromDraft(ProfileDraft draft) =>
        new(draft.Network, draft.Username, draft.Url);
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

    public static ImportExperienceRequest FromDraft(ExperienceDraft draft) => new(
        draft.Type, draft.Organization, draft.Position, draft.Start, draft.End, draft.Summary, draft.Highlights);
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

    public static ImportEducationRequest FromDraft(EducationDraft draft) => new(
        draft.Institution, draft.Degree, draft.FieldOfStudy, draft.Start, draft.End, draft.Grade, draft.Level);
}

public sealed record ImportSkillRequest(
    string? Name = null,
    string? Level = null,
    string? YearsOfExperience = null)
{
    public SkillDraft ToDraft() => new(Name, Level, YearsOfExperience);

    public static ImportSkillRequest FromDraft(SkillDraft draft) =>
        new(draft.Name, draft.Level, draft.YearsOfExperience);
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

    public static ImportProjectRequest FromDraft(ProjectDraft draft) => new(
        draft.Name, draft.Start, draft.End, draft.Description, draft.RepositoryUrl, draft.LiveDemoUrl,
        draft.Technologies, draft.Highlights);
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

    public static ImportCertificateRequest FromDraft(CertificateDraft draft) => new(
        draft.Name, draft.Issuer, draft.CredentialId, draft.CredentialUrl, draft.ValidityStart, draft.ValidityEnd);
}

public sealed record ImportLanguageRequest(
    string? Name = null,
    string? Fluency = null,
    string? Level = null)
{
    public LanguageDraft ToDraft() => new(Name, Fluency, Level);

    public static ImportLanguageRequest FromDraft(LanguageDraft draft) =>
        new(draft.Name, draft.Fluency, draft.Level);
}

public sealed record ImportAwardRequest(
    string? Title = null,
    string? Awarder = null,
    string? Date = null,
    string? Summary = null)
{
    public AwardDraft ToDraft() => new(Title, Awarder, Date, Summary);

    public static ImportAwardRequest FromDraft(AwardDraft draft) =>
        new(draft.Title, draft.Awarder, draft.Date, draft.Summary);
}

public sealed record ImportPublicationRequest(
    string? Title = null,
    string? Publisher = null,
    string? Url = null,
    string? ReleaseDate = null,
    string? Summary = null)
{
    public PublicationDraft ToDraft() => new(Title, Publisher, Url, ReleaseDate, Summary);

    public static ImportPublicationRequest FromDraft(PublicationDraft draft) =>
        new(draft.Title, draft.Publisher, draft.Url, draft.ReleaseDate, draft.Summary);
}

public sealed record ImportInterestRequest(
    string? Name = null,
    IReadOnlyList<string?>? Keywords = null)
{
    public InterestDraft ToDraft() => new(Name, Keywords);

    public static ImportInterestRequest FromDraft(InterestDraft draft) => new(draft.Name, draft.Keywords);
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

    public static ImportReferenceRequest FromDraft(ReferenceDraft draft) => new(
        draft.Name, draft.Position, draft.Company, draft.Email, draft.PhoneNumber, draft.ReferenceText);
}

// The extraction wire shape, mapped from the Application's DocumentExtraction rather than reusing it —
// wire contracts are this layer's own types, per the repo rule. PageCount is null for every format
// except PDF, the only one that states its page count (see DocumentExtraction.PageCount).
public sealed record ExtractDocumentTextResponse(
    string Text,
    int? PageCount,
    IReadOnlyList<string> Warnings);

// The response of POST /resumes/import/propose: a best-effort draft AND the confidence beside it. The
// draft is an ImportResumeRequest — the exact shape the review screen posts back to POST /resumes/import
// after the candidate corrects it. Confidence is a SEPARATE structure and is deliberately not part of the
// draft, so a client posting the corrected draft back cannot (and need not) send confidence with it.
//
// THE IMPORT EVIDENCE GOES THE OTHER WAY, and it is INSIDE the draft rather than beside it. Confidence is
// advice to the review screen and must never cross back; the evidence must, because it is what lets the
// readability engine grade the uploaded document, and it is signed precisely so that crossing back
// through a client is safe. Putting it on the draft means the review screen's contract stays "post this
// object back" — a client that had to lift a sibling field into the body is a client that will forget to.
public sealed record ProposeResumeDraftResponse(
    ImportResumeRequest Draft,
    DraftConfidenceResponse Confidence)
{
    public static ProposeResumeDraftResponse FromProposal(
        ResumeDraftProposal proposal, string? importEvidence) => new(
        ImportResumeRequest.FromDraft(proposal.Draft) with { ImportEvidence = importEvidence },
        DraftConfidenceResponse.FromConfidence(proposal.Confidence));
}

// Overall and each field's confidence are strings on the wire (the enum names), so the API contract does
// not leak the CLR enum and a client reads "NotExtracted" / "Low" / "Medium" / "High" directly.
public sealed record DraftConfidenceResponse(
    string Overall,
    IReadOnlyList<FieldConfidenceResponse> Fields,
    IReadOnlyList<string> Warnings)
{
    public static DraftConfidenceResponse FromConfidence(DraftConfidence confidence) => new(
        confidence.Overall.ToString(),
        confidence.Fields.Select(FieldConfidenceResponse.FromProvenance).ToList(),
        confidence.Warnings);
}

/// <param name="Suggestion">
/// A corrected value the review screen may offer as one click, or null when there is nothing safe to
/// propose. <b>Never applied by this API</b>: the candidate accepts it and posts the result back through
/// <c>POST /v1/resumes/import</c>, which validates exactly as strictly as it does for a hand-typed
/// value. The machine does the typing; the person does the deciding; nothing skips validation.
/// </param>
public sealed record FieldConfidenceResponse(
    string Path,
    string Confidence,
    string? SourceText,
    string? Suggestion)
{
    public static FieldConfidenceResponse FromProvenance(FieldProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return new(
            provenance.Path,
            provenance.Confidence.ToString(),
            provenance.SourceText,
            provenance.Suggestion);
    }
}

namespace BuildCv.Api.Contracts;

using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;

// The wire shape of the candidate profile — the data a CV is generated FROM, owned by the account
// rather than by any one resume. It is the same shape as ResumeResponse, entry ids included, because
// the profile is edited like a CV is: every collection needs "which bullet point is this one" so a
// later PUT or DELETE can name it. The per-entry mappers are the resume's own — the item types are
// shared with Resume by design, so one encoding serves both aggregates and they cannot drift.
public sealed record CandidateProfileResponse(
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
    /// <param name="loaded">
    /// The profile and the identity of its entries, from ONE load. Taken as a pair rather than as two
    /// arguments because <see cref="ResumeItemIds"/>' positional alignment is only true within a single
    /// materialization — two arguments would let a caller pair ids with a different read and hand a
    /// candidate an id that names somebody else's bullet point.
    /// </param>
    public static CandidateProfileResponse From(CandidateProfileWithItemIds loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        var profile = loaded.Profile;
        var ids = loaded.ItemIds;

        return new CandidateProfileResponse(
            profile.Id.Value,
            profile.OwnerId.Value,
            ContactInformationResponse.From(profile.ContactInformation),
            profile.CreatedAt,
            profile.UpdatedAt,
            ItemIdProjection.Project("Candidate profile", profile.Experiences, ids.For(ResumeSection.Experiences), ExperienceResponse.From),
            ItemIdProjection.Project("Candidate profile", profile.Educations, ids.For(ResumeSection.Educations), EducationResponse.From),
            ItemIdProjection.Project("Candidate profile", profile.Skills, ids.For(ResumeSection.Skills), SkillResponse.From),
            ItemIdProjection.Project("Candidate profile", profile.Projects, ids.For(ResumeSection.Projects), ProjectResponse.From),
            ItemIdProjection.Project("Candidate profile", profile.Certificates, ids.For(ResumeSection.Certificates), CertificateResponse.From),
            ItemIdProjection.Project("Candidate profile", profile.Languages, ids.For(ResumeSection.Languages), LanguageResponse.From),
            ItemIdProjection.Project("Candidate profile", profile.Awards, ids.For(ResumeSection.Awards), AwardResponse.From),
            ItemIdProjection.Project("Candidate profile", profile.Publications, ids.For(ResumeSection.Publications), PublicationResponse.From),
            ItemIdProjection.Project("Candidate profile", profile.Interests, ids.For(ResumeSection.Interests), InterestResponse.From),
            ItemIdProjection.Project("Candidate profile", profile.References, ids.For(ResumeSection.References), ReferenceResponse.From));
    }
}

// The shape every profile WRITE answers with: the contact basics, the timestamps and the size of each
// section — never the entries, and therefore no entry ids. It mirrors what the resume routes answer
// (ResumeSummaryResponse) for the same reason: a freshly-written entry's id would need a re-fetch to
// be honest, and the only route that hands ids out is GET /v1/profile.
public sealed record CandidateProfileSummaryResponse(
    Guid Id,
    Guid OwnerId,
    string FullName,
    string Email,
    string? Location,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ResumeSectionCounts Counts)
{
    public static CandidateProfileSummaryResponse From(CandidateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new CandidateProfileSummaryResponse(
            profile.Id.Value,
            profile.OwnerId.Value,
            profile.ContactInformation.FullName.Value,
            profile.ContactInformation.Email.Value,
            profile.ContactInformation.Location,
            profile.CreatedAt,
            profile.UpdatedAt,
            ResumeSectionCounts.From(profile));
    }
}

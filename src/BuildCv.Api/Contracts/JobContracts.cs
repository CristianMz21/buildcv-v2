namespace BuildCv.Api.Contracts;

using BuildCv.Domain.Jobs;

public sealed record CreateJobRequest(
    string Title,
    string CompanyName,
    Guid? CompanyId,
    string? Description);

// The wire shape of a job posting. It exists for the same reason AnalysisResponse does, one endpoint
// over: /jobs returned the JobPosting AGGREGATE, which CLAUDE.md forbids, and this chain put TWO NEW
// ENUMS on that aggregate — JobPosting.EducationLevel and LanguageRequirement.MinimumLevel. With no
// global JsonStringEnumConverter registered anywhere (ScoringContractTests measures that, which is
// why every name in this file comes from ToString() rather than a converter), both were shipping as
// raw integers, and their numbers are documented in Domain as an append-only PERSISTENCE detail. The
// moment a client binds to them they are a public API contract too and renumbering breaks it.
//
// FREE NOW, BREAKING LATER, and that is the whole argument for doing it in this chain rather than the
// next: no endpoint can set either field yet — CreateJobRequest carries Title, CompanyName, CompanyId
// and Description, there is no update endpoint, and JobPosting.SetEducationLevel and
// AddLanguageRequirement have no caller in src/ — so `educationLevel` is null and
// `languageRequirements` is [] on every posting the API can produce. Nobody has read either number
// yet. Once the authoring endpoints land, changing it is a client-visible break.
//
// THE PRE-CHAIN RESPONSE IS OTHERWISE REPRODUCED KEY FOR KEY, field order included, captured off the
// live endpoint before this type existed:
//
//   - `status` stays an INTEGER, and so does `requirements[].priority`. Both predate this chain and
//     have clients. The inconsistency with the two named enums below is deliberate and is the same
//     trade AnalysisResponse made with `band`: flipping a pre-existing encoding is its own repo-wide
//     change, and a consistent bad convention beats a half-flipped one.
//   - Ids keep their {"value": guid} envelope, `companyName` its {"value": string} and
//     `requirements[].skill` its {"name": string}, because that is what a strongly-typed Domain record
//     already serialized into. Declared here rather than left to the Domain types so a refactor there
//     cannot change the wire by accident.
//   - `educationLevel` and `languageRequirements[].minimumLevel` carry NAMES: "Bachelor", not 2. They
//     belong to this chain, are unmerged, and have no clients, so naming them is still free.
public sealed record JobPostingResponse(
    IdEnvelope Id,
    IdEnvelope OwnerId,
    string Title,
    string? Description,
    IdEnvelope? CompanyId,
    CompanyNameEnvelope? CompanyName,
    int Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ClosesAt,
    IReadOnlyList<JobRequirementResponse> Requirements,
    IReadOnlyList<ResponsibilityResponse> Responsibilities,
    IReadOnlyList<LanguageRequirementResponse> LanguageRequirements,
    string? EducationLevel)
{
    public static JobPostingResponse From(JobPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);

        return new JobPostingResponse(
            new IdEnvelope(posting.Id.Value),
            new IdEnvelope(posting.OwnerId.Value),
            posting.Title,
            posting.Description,
            posting.CompanyId is { } companyId ? new IdEnvelope(companyId.Value) : null,
            posting.CompanyName is { } companyName ? new CompanyNameEnvelope(companyName.Value) : null,
            (int)posting.Status,
            posting.CreatedAt,
            posting.UpdatedAt,
            posting.PublishedAt,
            posting.ClosesAt,
            [.. posting.Requirements.Select(JobRequirementResponse.From)],
            [.. posting.Responsibilities.Select(ResponsibilityResponse.From)],
            [.. posting.LanguageRequirements.Select(LanguageRequirementResponse.From)],
            // Null stays null. "Not stated" and "HighSchool" are different claims about a posting and
            // the Domain keeps them apart on purpose; a default here would invent a requirement.
            posting.EducationLevel?.ToString());
    }
}

// The {"value": string} wrapper OrganizationName already serialized into, kept because it predates
// this chain. Separate from IdEnvelope rather than generic: the two carry different types and one of
// them is a Guid, so a shared shape would only look like reuse.
public sealed record CompanyNameEnvelope(string Value);

public sealed record JobRequirementResponse(TechnologyEnvelope Skill, int Priority, double Weight)
{
    // `priority` stays an integer: RequirementPriority predates this chain and ships that way today.
    public static JobRequirementResponse From(JobRequirement requirement) =>
        new(new TechnologyEnvelope(requirement.Skill.Name), (int)requirement.Priority, requirement.Weight);
}

public sealed record TechnologyEnvelope(string Name);

public sealed record ResponsibilityResponse(string Description)
{
    public static ResponsibilityResponse From(Responsibility responsibility) =>
        new(responsibility.Description);
}

/// <param name="MinimumLevel">
/// The proficiency name — <c>"Professional"</c>, never <c>2</c>. The numbers behind
/// <c>LanguageProficiency</c> are an append-only persistence detail and stating them on the wire
/// would make renumbering a breaking API change as well as a migration.
/// </param>
public sealed record LanguageRequirementResponse(string Name, string MinimumLevel)
{
    public static LanguageRequirementResponse From(LanguageRequirement requirement) =>
        new(requirement.Name, requirement.MinimumLevel.ToString());
}

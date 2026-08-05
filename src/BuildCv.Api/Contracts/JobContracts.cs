namespace BuildCv.Api.Contracts;

using BuildCv.Domain.Jobs;

public sealed record CreateJobRequest(
    string Title,
    string CompanyName,
    Guid? CompanyId,
    string? Description);

// The wire shape of a job posting. It exists for the same reason AnalysisResponse does, one endpoint
// over: /jobs returned the JobPosting AGGREGATE, which CLAUDE.md forbids, and its enums were shipping
// as raw integers whose numbers are documented in Domain as an append-only PERSISTENCE detail. The
// moment a client binds to them they are a public API contract too and renumbering breaks it.
//
// EVERY ENUM ON THIS RESPONSE CARRIES ITS NAME AND EVERY ID IS A BARE GUID — the same v1 settlement
// stated on AnalysisResponse, applied to the shapes this file used to defer. It is a claim about the
// endpoints that map through a DTO, which as of this commit is /jobs and /scoring; see
// AnalysisResponse for the routes still answering with an aggregate.
//
//   - `status` and `requirements[].priority` shipped as integers and are names now: "Draft", never 0,
//     from ToString(), so a JsonStringEnumConverter registered later cannot change the wire and a
//     Domain renumbering stays a migration instead of a client break.
//   - Ids lost their {"value": guid} envelope, `companyName` its {"value": string}, and
//     `requirements[].skill` its {"name": string}. All three were what a strongly-typed Domain record
//     happened to serialize into, not decisions. Unwrapping `skill` also makes it round-trip: the
//     /v1/job-offers/extract proposal, the /v1/job-offers/import request and this response all say
//     `"skill": "React"`, where the wrapper made the read side disagree with both write sides.
//   - `educationLevel` and `languageRequirements[].minimumLevel` carried names from the day the DTO
//     landed and are unchanged.
//
// All of it was free because no client existed when /v1 shipped; the moment one binds, any of these
// is a /v2. The mapping stays declared here, field by field, so a Domain refactor cannot change the
// wire by accident.
public sealed record JobPostingResponse(
    Guid Id,
    Guid OwnerId,
    string Title,
    string? Description,
    Guid? CompanyId,
    string? CompanyName,
    string Status,
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
            posting.Id.Value,
            posting.OwnerId.Value,
            posting.Title,
            posting.Description,
            posting.CompanyId?.Value,
            posting.CompanyName?.Value,
            posting.Status.ToString(),
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

// `skill` is the technology name as a bare string — "C#", not {"name": "C#"} — matching what
// /v1/job-offers/extract proposes and what /v1/job-offers/import accepts, so a requirement
// round-trips without a shape change. `priority` is the RequirementPriority name.
public sealed record JobRequirementResponse(string Skill, string Priority, double Weight)
{
    public static JobRequirementResponse From(JobRequirement requirement) =>
        new(requirement.Skill.Name, requirement.Priority.ToString(), requirement.Weight);
}

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

namespace BuildCv.Api.Contracts;

using BuildCv.Application.Jobs;

// The wire request for POST /job-offers/import. It mirrors JobOfferDraft BY HAND -- an Application type
// is never a wire contract (CLAUDE.md) -- so a swapped field here is a real bug, which is why the import
// round-trip test sends a distinct value in every field. Every leaf is a nullable string for the same
// reason ResumeDraft's are: a malformed value must come back as a field error with a path, not a bare
// model-binding 400 that names nothing.
public sealed record ImportJobOfferRequest(
    string? Title,
    string? CompanyName,
    IReadOnlyList<ImportJobRequirementItem?>? Requirements)
{
    public JobOfferDraft ToDraft() =>
        new(Title, CompanyName, Requirements?.Select(item => item?.ToDraft()).ToList());
}

public sealed record ImportJobRequirementItem(string? Skill, string? Priority)
{
    public JobRequirementDraft ToDraft() => new(Skill, Priority);
}

// The paste a candidate makes on the review screen: the raw offer text they copied from a job board.
public sealed record ExtractJobOfferRequirementsRequest(string? Text);

public sealed record ExtractJobOfferRequirementsResponse(IReadOnlyList<ProposedRequirementResponse> Requirements)
{
    public static ExtractJobOfferRequirementsResponse From(IReadOnlyList<ProposedRequirement> proposed) =>
        new([.. proposed.Select(ProposedRequirementResponse.From)]);
}

// `priority` carries the NAME ("NiceToHave"), never the number, matching how JobPostingResponse names
// the two enums this chain added and for the same reason: RequirementPriority's numbers are an
// append-only persistence detail, and stating them on the wire would make renumbering a breaking change.
// `priorityGuessed` tells the review screen the priority is a default to promote, not something read
// from the offer text.
public sealed record ProposedRequirementResponse(string Skill, string Priority, bool PriorityGuessed)
{
    public static ProposedRequirementResponse From(ProposedRequirement proposed) =>
        new(proposed.Skill, proposed.Priority.ToString(), proposed.PriorityGuessed);
}

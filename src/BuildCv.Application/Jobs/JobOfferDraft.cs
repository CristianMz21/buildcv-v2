namespace BuildCv.Application.Jobs;

// A JOB OFFER as a candidate holds it on a review screen, on its way to becoming a candidate-owned
// Draft JobPosting. The same discipline as ResumeDraft: EVERY LEAF IS A NULLABLE STRING, including the
// requirement priority, so no VALUE can be rejected at model binding where a failure names no field and
// collects no siblings. A malformed priority arrives intact and comes back as a field error with a path.
//
// It is deliberately SMALL. A candidate's offer exists to be SCORED AGAINST, so it carries only what the
// scoring engine reads from a posting: a title, the company, and the skill requirements. It does NOT
// carry the free-text description, responsibilities, an education level or a language requirement:
//
//   - the description is not a scoring input and storing it would only add a bounded-column length rule
//     to restate here;
//   - SetEducationLevel and SetLanguageRequirements remain endpoint-less on purpose (see JobPosting), and
//     a language requirement in particular is the one field that makes the ScoringWeightsSnapshot
//     "unreadable to a pre-PR-1 reader" branch reachable on real data -- documented on that type as
//     currently unreachable, and kept that way here.
//
// A candidate who wants those authoring affordances is asking for the recruiter surface, which is a
// different resource with a different owner and lifecycle.
public sealed record JobOfferDraft(
    string? Title = null,
    string? CompanyName = null,
    IReadOnlyList<JobRequirementDraft?>? Requirements = null);

// Priority is a nullable string parsed against RequirementPriority. Left blank it defaults to the
// CONSERVATIVE NiceToHave, never MustHave -- the same rule the extractor applies, and for the same
// reason: MustHave is the gate that drives Critical advice, and guessing it wrong inflates a
// recommendation list into "everything is Critical". Weight is not on the draft at all: it derives from
// Priority in JobRequirement.Create and the two must never be able to contradict each other.
public sealed record JobRequirementDraft(
    string? Skill = null,
    string? Priority = null);

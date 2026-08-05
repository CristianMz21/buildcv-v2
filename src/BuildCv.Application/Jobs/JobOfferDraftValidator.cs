namespace BuildCv.Application.Jobs;

using BuildCv.Application.Common;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using static BuildCv.Application.Common.FieldErrorCollector;

/// <summary>
/// Turns a reviewed <see cref="JobOfferDraft"/> into either a candidate-owned Draft
/// <see cref="JobPosting"/> or the full list of <see cref="FieldError"/>s that stopped it.
/// </summary>
/// <remarks>
/// The SAME mechanism as <c>ResumeDraftValidator</c> — validation and construction are one pass through
/// <see cref="FieldErrorCollector"/>, every verdict is a real Domain factory's throw, and it is
/// all-or-nothing. This class holds only the job-offer-specific walk; the domain-agnostic helpers are
/// shared, which is what keeps the two importers one mechanism rather than two.
/// </remarks>
public static class JobOfferDraftValidator
{
    // A posting with 100 distinct skill requirements is already an order of magnitude past any real
    // offer. The cap declines building thousands of value objects from a runaway paste; it is not a
    // limit on a genuine offer, and it matches the spirit of ResumeDraftLimits.
    private const int MaxRequirements = 100;

    // No existing endpoint answers a message for a bad RequirementPriority (POST /jobs cannot set one),
    // so this is a new string in the family of "Invalid skill level." / "Invalid education level." that
    // ResumeDraftValidator already emits.
    private const string InvalidPriorityMessage = "Invalid requirement priority.";

    // Stands in for a company name that failed to build, so the posting can still be constructed and its
    // title validated in the same pass. It cannot escape: it is used only after an error was recorded,
    // and the single return at the end hands back a rejection whenever the error list is non-empty.
    private static readonly OrganizationName UnusableCompany = OrganizationName.Create("unused");

    public static JobOfferImportResult Validate(AccountId ownerId, JobOfferDraft draft)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(draft);

        var errors = new List<FieldError>();

        var companyName = BuildRequired("companyName", draft.CompanyName, OrganizationName.Create, errors);

        // JobPosting.Create validates the TITLE itself (required, and no longer than its column), so it
        // is the single construction that decides the title's validity, reported at "title". The
        // pre-built companyName is passed in, so it is never the value that throws here. When the title
        // is unbuildable the `?? ` stand-in keeps a posting to add the requirements to -- an error was
        // recorded, so the all-or-nothing gate discards it, exactly as ResumeDraftValidator's
        // UnusableContact is discarded. The stand-in cannot throw: "unused" is a valid title.
        var posting =
            Build("title", errors, () => JobPosting.Create(ownerId, draft.Title!, companyName ?? UnusableCompany))
            ?? JobPosting.Create(ownerId, "unused", UnusableCompany);

        AddRequirements(posting, draft.Requirements, errors);

        return errors.Count == 0
            ? JobOfferImportResult.Imported(posting)
            : JobOfferImportResult.Rejected(errors);
    }

    private static void AddRequirements(
        JobPosting posting, IReadOnlyList<JobRequirementDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "requirements", MaxRequirements, errors, (item, path) =>
        {
            var skill = BuildRequired($"{path}.skill", item.Skill, Technology.Create, errors);

            // Blank priority defaults to the CONSERVATIVE NiceToHave, never MustHave -- a blank on a
            // review screen must not silently become the gate that drives Critical advice. A NON-blank
            // but unknown value is a field error at requirements[i].priority.
            var priority =
                ParseOptionalEnum<RequirementPriority>($"{path}.priority", item.Priority, InvalidPriorityMessage, errors)
                ?? RequirementPriority.NiceToHave;

            if (skill is null)
                return;

            // JobRequirement.Create and AddRequirement both run inside the harness. Weight is NOT passed:
            // Create derives it from Priority so the two can never contradict each other. The duplicate
            // guard on AddRequirement is case-insensitive on the skill NAME, and because the walk is in
            // draft order the first occurrence is already on the posting and it is the LATER one that
            // throws -- the line the candidate deletes, which is the index the path carries. This mirrors
            // ResumeDraftValidator.AddSkills exactly.
            Add($"{path}.skill", errors, () => posting.AddRequirement(JobRequirement.Create(skill, priority)));
        });
}

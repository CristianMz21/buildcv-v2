using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Jobs;
using BuildCv.Domain.Common.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace BuildCv.Api.Endpoints;

// A CANDIDATE'S PRIVATE JOB OFFER, deliberately its own resource rather than part of /jobs.
//
// /jobs is the recruiter surface: a posting there is owned by an organization, published to a board and
// managed through a lifecycle, and POST /jobs is gated by the Recruiter policy. A candidate's offer is
// none of those -- it is a private note about one opportunity that exists only to be scored against. It
// shares the JobPosting Domain type, but a shared aggregate is not a shared resource, so it lives under
// its own /job-offers group gated by the Candidate policy (which admits candidates, recruiters and
// admins). Putting these routes on /jobs would either force the Recruiter policy onto a candidate action
// or quietly widen /jobs to candidates; a separate group keeps each surface's authorization honest.
public static class JobOfferEndpoints
{
    // Exposed so a test can post exactly one byte over it rather than restating the number. A job offer
    // is tiny -- a title, a company and up to a hundred short skill requirements -- so this ceiling is a
    // fraction of the resume import's and far below the framework default; it exists so a runaway
    // requirements array is refused by the server before JobOfferDraftValidator's cap has to walk it.
    public const long ImportRequestSizeLimitBytes = 256 * 1024;

    public static RouteGroupBuilder MapJobOfferEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/job-offers")
            .WithTags("Job offers")
            .RequireAuthorization(AuthorizationPolicies.Candidate);

        // The confirmed draft in, a candidate-owned Draft posting out. 201 with the same JobPostingResponse
        // /jobs answers, because it is the same aggregate; a rejected draft answers the standard
        // ProblemDetails validation shape, keyed by JSON field path, exactly like POST /v1/resumes/import.
        //
        // The Location points at GET /v1/jobs/{id}, which admits the owner -- so the candidate can read back
        // the offer they just created. The posting is a Draft, scorable by its owner and by nobody else
        // until published, and PublishJobPostingHandler refuses a candidate the publish.
        group.MapPost("/import", async Task<IResult> (
            ImportJobOfferRequest request,
            ICommandHandler<ImportJobOfferCommand, JobOfferImportResult> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new ImportJobOfferCommand(httpContext.User.GetAccountId(), request.ToDraft()),
                cancellationToken);

            return result.IsSuccess
                ? Results.Created(
                    $"/v1/jobs/{result.JobPosting!.Id.Value}", JobPostingResponse.From(result.JobPosting))
                : result.FieldErrors.ToValidationProblem();
        })
        // The framework enforces IRequestSizeLimitMetadata on its own for minimal APIs, chunked bodies
        // included -- measured on the resume import, do not reintroduce a middleware for it.
        .WithMetadata(new RequestSizeLimitAttribute(ImportRequestSizeLimitBytes))
        .WithSummary("Creates a candidate-owned Draft job offer from one reviewed draft.")
        .WithDescription(
            "Every field is sent as a STRING, including the requirement priority, so no VALUE is rejected "
            + "at model binding: an unknown priority comes back as a field error rather than a framework "
            + "400 naming nothing. Validation is all-or-nothing and collects EVERY bad field in one pass, "
            + "keyed by JSON field path (`requirements[2].skill`). The posting is a private Draft owned by "
            + "the caller: scorable by them via POST /v1/scoring/score and by nobody else, and it CANNOT be "
            + "published -- publishing is a recruiter action. A requirement priority left blank defaults "
            + "to NiceToHave; duplicate skills are reported against the LATER occurrence.");

        // Pasted offer text in, PROPOSED skill requirements out. It creates nothing: the candidate edits
        // the proposals and posts the confirmed set to /import. Priorities come back as NiceToHave with
        // priorityGuessed = true, because the extractor never reads a priority from the text -- it will
        // not guess a must-have.
        group.MapPost("/extract", async Task<IResult> (
            ExtractJobOfferRequirementsRequest request,
            IQueryHandler<ExtractJobOfferRequirementsQuery, Result<IReadOnlyList<ProposedRequirement>>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new ExtractJobOfferRequirementsQuery(request.Text ?? string.Empty), cancellationToken);

            return result.ToHttpResult(
                proposed => Results.Ok(ExtractJobOfferRequirementsResponse.From(proposed)));
        })
        .WithSummary("Proposes skill requirements from pasted job-offer text, for the candidate to confirm.")
        .WithDescription(
            "Recognises common technology names in the text and proposes them as NiceToHave requirements, "
            + "each marked priorityGuessed. It PROPOSES only: nothing is created, and /import reads the "
            + "confirmed draft the candidate submits, not this proposal. It recognises a curated "
            + "vocabulary case-sensitively and will miss skills it does not know -- the candidate adds "
            + "those -- because a missed skill is visible on the review screen and an invented one is not.");

        return group;
    }
}

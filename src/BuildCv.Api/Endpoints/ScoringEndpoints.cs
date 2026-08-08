using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Scoring;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

namespace BuildCv.Api.Endpoints;

public static class ScoringEndpoints
{
    public static RouteGroupBuilder MapScoringEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scoring").WithTags("Scoring");

        group.MapPost("/score", async (
            ScoreResumeRequest request,
            ICommandHandler<ScoreResumeCommand, Result<AnalysisView>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new ScoreResumeCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(request.ResumeId),
                new JobPostingId(request.JobPostingId)), cancellationToken);

            // A pure mapping. The endpoint used to return the Analysis aggregate straight out, which
            // put RecommendationKind and RecommendationPriority on the wire as raw integers — numbers
            // this repo documents as an append-only persistence detail. AnalysisResponse states the
            // contract instead, and the ordering the aggregate deliberately does not guarantee.
            return result.ToHttpResult(view => Results.Ok(AnalysisResponse.From(view.Analysis, view.IsStale)));
        })
        .WithSummary("Scores a resume against a job posting, reusing an identical run rather than repeating it.")
        .WithDescription(
            "DE-DUPLICATED. If this resume was already scored against this posting, and neither has been "
            + "edited since, and the scoring model has not changed, and it was scored TODAY, the stored "
            + "run is returned instead of a new one — same `id`, same `scoredAt`, and no new entry in "
            + "`GET /v1/resumes/{id}/analyses`. So a repeated request is not a no-op and not an error: it "
            + "is the same scoring event. The date is part of that test because a score genuinely moves "
            + "with time — experience accrues and certificates expire — so tomorrow's identical request "
            + "does produce a new run. `isStale` on the response is therefore always false here. "
            + ZeroWeightContract);

        // Reading one score back. Same DTO as /scoring/score, deliberately: a second shape for the same
        // aggregate is how two contracts for one thing start, and a candidate comparing what they were
        // told last week with what they are told today must be comparing like with like.
        //
        // AnalysisResponse.From is also where the recommendations get their order. It matters more here
        // than on the POST: a freshly built Analysis still carries the order the Application layer sorted
        // it into, while one loaded from the database is an honest SET — the Recommendations table has no
        // Rank column by design — so without that call this endpoint would render whatever order the
        // server happened to return, differently between two reads of the same row.
        group.MapGet("/{analysisId:guid}", async (
            Guid analysisId,
            IQueryHandler<GetAnalysisByIdQuery, Result<AnalysisView>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new GetAnalysisByIdQuery(
                httpContext.User.GetAccountId(), new AnalysisId(analysisId)), cancellationToken);

            return result.ToHttpResult(view => Results.Ok(AnalysisResponse.From(view.Analysis, view.IsStale)));
        })
        .WithSummary("Returns one stored analysis, readable only by the owner of the resume it scored.")
        .WithDescription(
            "An analysis has no owner of its own: it belongs to a resume, and that resume's owner is the "
            + "only account that may read it. Deleting the resume hides every score derived from it, so a "
            + "previously readable id then answers 404. "
            + StalenessContract
            + ZeroWeightContract);

        return group;
    }

    // Stated on the two endpoints that can actually answer `true`. POST /scoring/score cannot — the run it
    // returns was either just computed from the current resume or reused precisely because the resume had
    // not moved — so its own description says that instead, and repeating this there would invite a client
    // to branch on a value that is constant.
    private const string StalenessContract =
        "`isStale` IS COMPUTED PER REQUEST AND NEVER STORED. It is true when the resume has been edited "
        + "since this score was taken — the number describes a CV the candidate no longer has — and ALSO "
        + "true when the analysis predates the columns that record what it scored and simply cannot say. "
        + "Unknown is reported as stale rather than as current, deliberately: the cost of that answer is a "
        + "re-score, while the opposite misleads. Only the RESUME side raises it; editing the job posting "
        + "does not, so a client comparing two scores across a posting change must still read the weights. "
        + "Re-posting to /v1/scoring/score is what clears it. ";

    // Stated on every endpoint that returns an AnalysisResponse, because this is the one field pairing a
    // client developer will otherwise read as a bug report from the candidate.
    //
    // "Expressed no weighted requirement" rather than "stated no requirement", and the difference is
    // reachable rather than pedantic: a posting may state requirements and weight all of them 0.0, which
    // renormalizes the section out while `recommendations[]` still names those requirements. That case is
    // executed by RecommendationBuilderTests.ZeroWeightedRequirements_stillProduceAdviceWithAnHonestZero
    // Impact, so "the posting asked nothing" would be a false explanation of a state the domain reaches.
    private const string ZeroWeightContract =
        "A section whose `breakdown.weights.<section>` is 0 EXPRESSED NO WEIGHTED REQUIREMENT: it "
        + "neither helped nor hurt the score, and the `score` beside it measures nothing. Two different "
        + "postings land there — one that asked nothing of the section, and one that asked and weighted "
        + "every requirement 0.0. In the second, `recommendations[]` still names those requirements, each "
        + "with an `impact` of 0, so a section can carry no weight and still carry advice. There is no "
        + "separate flag for any of this — the weight IS the signal, deliberately, so the two can never "
        + "disagree. The remaining weights are renormalized to still total 1.0, which is why an "
        + "`overallScore` of 0 with only three recommendations is a complete answer rather than a "
        + "truncated one. "
        + "`weights.languages` IS 0 ON EVERY ANALYSIS this build can produce: no endpoint puts a language "
        + "requirement on a posting. `weights.skills` is NO LONGER always 0 — `POST /v1/job-offers/import` "
        + "lets a candidate state skill requirements on their own Draft offer, so an analysis scored "
        + "against an imported offer carries a nonzero skills weight, while one scored against a `POST "
        + "/v1/jobs` posting (title, company and description only) still carries 0. Read the weight per "
        + "analysis rather than assuming either is always 0.";
}

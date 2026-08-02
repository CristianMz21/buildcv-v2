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
            ICommandHandler<ScoreResumeCommand, Result<Analysis>> handler,
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
            return result.ToHttpResult(analysis => Results.Ok(AnalysisResponse.From(analysis)));
        })
        .WithSummary("Scores a resume against a job posting and stores the result.")
        .WithDescription(ZeroWeightContract);

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
            IQueryHandler<GetAnalysisByIdQuery, Result<Analysis>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new GetAnalysisByIdQuery(
                httpContext.User.GetAccountId(), new AnalysisId(analysisId)), cancellationToken);

            return result.ToHttpResult(analysis => Results.Ok(AnalysisResponse.From(analysis)));
        })
        .WithSummary("Returns one stored analysis, readable only by the owner of the resume it scored.")
        .WithDescription(
            "An analysis has no owner of its own: it belongs to a resume, and that resume's owner is the "
            + "only account that may read it. Deleting the resume hides every score derived from it, so a "
            + "previously readable id then answers 404. "
            + ZeroWeightContract);

        return group;
    }

    // Stated on every endpoint that returns an AnalysisResponse, because this is the one field pairing a
    // client developer will otherwise read as a bug report from the candidate.
    private const string ZeroWeightContract =
        "A section whose `breakdown.weights.<section>` is 0 was NOT ASKED ABOUT by the posting: it "
        + "neither helped nor hurt the score, and the `score` beside it measures nothing. There is no "
        + "separate flag for this — the weight IS the signal, deliberately, so the two can never "
        + "disagree. The remaining weights are renormalized to still total 1.0, which is why an "
        + "`overallScore` of 0 with only three recommendations is a complete answer rather than a "
        + "truncated one.";
}

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
        });

        return group;
    }
}

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
            return result.ToHttpResult();
        });

        return group;
    }
}

using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Jobs;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;

namespace BuildCv.Api.Endpoints;

public static class JobEndpoints
{
    public static RouteGroupBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/jobs").WithTags("Jobs");

        group.MapPost("/", async (
            CreateJobRequest request,
            ICommandHandler<CreateJobPostingCommand, Result<JobPosting>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new CreateJobPostingCommand(
                httpContext.User.GetAccountId(),
                request.Title,
                request.CompanyName,
                request.CompanyId is { } companyId ? new OrganizationId(companyId) : null,
                request.Description), cancellationToken);
            return result.ToHttpResult(job => Results.Created($"/jobs/{job.Id.Value}", job));
        })
        .RequireAuthorization(AuthorizationPolicies.Recruiter);

        group.MapPost("/{id:guid}/publish", async (
            Guid id,
            HttpContext httpContext,
            ICommandHandler<PublishJobPostingCommand, Result<JobPosting>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new PublishJobPostingCommand(httpContext.User.GetAccountId(), new JobPostingId(id)), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/close", async (
            Guid id,
            HttpContext httpContext,
            ICommandHandler<CloseJobPostingCommand, Result<JobPosting>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new CloseJobPostingCommand(httpContext.User.GetAccountId(), new JobPostingId(id)), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            IQueryHandler<GetJobPostingQuery, Result<JobPosting>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new GetJobPostingQuery(httpContext.User.GetAccountId(), new JobPostingId(id)), cancellationToken);
            return result.ToHttpResult();
        });

        return group;
    }
}

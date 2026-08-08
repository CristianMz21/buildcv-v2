using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Organizations;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

namespace BuildCv.Api.Endpoints;

public static class OrganizationEndpoints
{
    public static RouteGroupBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/organizations").WithTags("Organizations");

        group.MapPost("/", async (
            CreateOrganizationRequest request,
            ICommandHandler<CreateOrganizationCommand, Result<Organization>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new CreateOrganizationCommand(
                httpContext.User.GetAccountId(),
                request.Name,
                request.Slug), cancellationToken);
            return result.ToHttpResult(org =>
                Results.Created($"/v1/organizations/{org.Id.Value}", OrganizationResponse.From(org)));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            IQueryHandler<GetOrganizationQuery, Result<Organization>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new GetOrganizationQuery(httpContext.User.GetAccountId(), new OrganizationId(id)), cancellationToken);
            return result.ToHttpResult(organization => Results.Ok(OrganizationResponse.From(organization)));
        });

        group.MapGet("/slug/{slug}", async (
            string slug,
            HttpContext httpContext,
            IQueryHandler<GetOrganizationBySlugQuery, Result<Organization>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new GetOrganizationBySlugQuery(httpContext.User.GetAccountId(), slug), cancellationToken);
            return result.ToHttpResult(organization => Results.Ok(OrganizationResponse.From(organization)));
        });

        group.MapPost("/{id:guid}/members", async Task<IResult> (
            Guid id,
            AddMemberRequest request,
            HttpContext httpContext,
            ICommandHandler<AddMemberCommand, Result<Organization>> handler,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse(request.Role, ignoreCase: true, out MembershipRole role))
                return Results.Problem(detail: "Invalid membership role.", statusCode: StatusCodes.Status400BadRequest);

            var result = await handler.Handle(new AddMemberCommand(
                httpContext.User.GetAccountId(),
                new OrganizationId(id),
                new AccountId(request.AccountId),
                role), cancellationToken);
            return result.ToHttpResult(organization => Results.Ok(OrganizationResponse.From(organization)));
        });

        group.MapDelete("/{id:guid}/members/{accountId:guid}", async (
            Guid id,
            Guid accountId,
            HttpContext httpContext,
            ICommandHandler<RemoveMemberCommand, Result<Organization>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new RemoveMemberCommand(
                httpContext.User.GetAccountId(),
                new OrganizationId(id),
                new AccountId(accountId)), cancellationToken);
            return result.ToHttpResult(organization => Results.Ok(OrganizationResponse.From(organization)));
        });

        return group;
    }
}

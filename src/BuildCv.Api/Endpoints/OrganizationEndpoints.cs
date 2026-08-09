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
        })
        .Produces<OrganizationResponse>(StatusCodes.Status201Created)
        .ProducesResultProblems()
        .ProducesAuthProblems();

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            IQueryHandler<GetOrganizationQuery, Result<Organization>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new GetOrganizationQuery(httpContext.User.GetAccountId(), new OrganizationId(id)), cancellationToken);
            return result.ToHttpResult(organization => Results.Ok(OrganizationResponse.From(organization)));
        })
        .Produces<OrganizationResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

        group.MapGet("/slug/{slug}", async (
            string slug,
            HttpContext httpContext,
            IQueryHandler<GetOrganizationBySlugQuery, Result<Organization>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new GetOrganizationBySlugQuery(httpContext.User.GetAccountId(), slug), cancellationToken);
            return result.ToHttpResult(organization => Results.Ok(OrganizationResponse.From(organization)));
        })
        .Produces<OrganizationResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

        group.MapPost("/{id:guid}/members", async Task<IResult> (
            Guid id,
            AddMemberRequest request,
            HttpContext httpContext,
            ICommandHandler<AddMemberCommand, Result<Organization>> handler,
            CancellationToken cancellationToken) =>
        {
            // IsDefined for the reason the four resume routes state: TryParse accepts ANY numeric
            // string, and Membership.Role is mapped to tinyint with an unchecked conversion
            // (OrganizationConfiguration), so "99" stores as 99, "300" truncates to 44 and "-1" wraps to
            // 255 — the same arithmetic #21 measured against SQL Server on the resume columns, which is
            // the same column type. Nothing downstream stopped it — AddMember takes the role as given —
            // so this route really did persist memberships whose role is a member of neither the enum
            // nor the column's intent, and RemoveMember's "cannot remove the only owner" rule reads
            // Role == Owner, which a 255 satisfies as "not an owner". BEHAVIOUR CHANGE: a numeric role
            // outside 0..2 answered 200 before this line and answers 400 after it.
            //
            // It closes undefined values only. TryParse still OR-combines comma-separated members on a
            // non-flags enum ("Owner,Member" is 0|2 = Member) and still accepts a leading sign ("+1" is
            // Admin); both yield a real member, which is what the column and every reader assume, and
            // both are pinned by EnumGuardTests rather than narrowed here — narrowing one of the six
            // parse sites in this API and not the other five is how two contracts for one input shape
            // get shipped.
            if (!Enum.TryParse(request.Role, ignoreCase: true, out MembershipRole role)
                || !Enum.IsDefined(role))
                return Results.Problem(detail: "Invalid membership role.", statusCode: StatusCodes.Status400BadRequest);

            var result = await handler.Handle(new AddMemberCommand(
                httpContext.User.GetAccountId(),
                new OrganizationId(id),
                new AccountId(request.AccountId),
                role), cancellationToken);
            return result.ToHttpResult(organization => Results.Ok(OrganizationResponse.From(organization)));
        })
        .Produces<OrganizationResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
        })
        .Produces<OrganizationResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

        return group;
    }
}

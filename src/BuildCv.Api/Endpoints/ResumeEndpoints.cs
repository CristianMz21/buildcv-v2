using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;

namespace BuildCv.Api.Endpoints;

public static class ResumeEndpoints
{
    public static RouteGroupBuilder MapResumeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/resumes")
            .WithTags("Resumes")
            .RequireAuthorization(AuthorizationPolicies.Candidate);

        group.MapPost("/", async Task<IResult> (
            CreateResumeRequest request,
            ICommandHandler<CreateResumeCommand, Result<Resume>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new CreateResumeCommand(
                httpContext.User.GetAccountId(),
                request.FullName,
                request.Email,
                request.PhoneNumber,
                request.Location,
                request.Summary), cancellationToken);
            return result.ToHttpResult(resume => Results.Created($"/resumes/{resume.Id.Value}", resume));
        });

        // Keyset paged, and there is no way to ask for the whole list: limit is clamped to a ceiling
        // and cursor is the only way forward. `limit` and `cursor` bind from the query string because
        // they are nullable simple types, which minimal APIs already treat as optional.
        group.MapGet("/", async (
            HttpContext httpContext,
            IQueryHandler<GetResumesByOwnerQuery, Result<Page<Resume>>> handler,
            CancellationToken cancellationToken,
            int? limit,
            string? cursor) =>
        {
            var requester = httpContext.User.GetAccountId();
            var result = await handler.Handle(
                new GetResumesByOwnerQuery(requester, requester, limit, cursor), cancellationToken);
            return result.ToHttpResult(page => Results.Ok(new PagedResponse<Resume>(page.Items, page.NextCursor)));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            IQueryHandler<GetResumeQuery, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new GetResumeQuery(httpContext.User.GetAccountId(), new ResumeId(id)), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPut("/{id:guid}/contact", async (
            Guid id,
            UpdateContactRequest request,
            HttpContext httpContext,
            ICommandHandler<UpdateContactInformationCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new UpdateContactInformationCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.FullName,
                request.Email,
                request.PhoneNumber,
                request.Location,
                request.Summary), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/skills", async Task<IResult> (
            Guid id,
            AddSkillRequest request,
            HttpContext httpContext,
            ICommandHandler<AddSkillCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            SkillLevel? level = null;
            if (request.Level is not null)
            {
                if (!Enum.TryParse(request.Level, ignoreCase: true, out SkillLevel parsed))
                    return Results.Problem(detail: "Invalid skill level.", statusCode: StatusCodes.Status400BadRequest);
                level = parsed;
            }

            var result = await handler.Handle(new AddSkillCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.SkillName,
                level,
                request.YearsOfExperience), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/experiences", async Task<IResult> (
            Guid id,
            AddExperienceRequest request,
            HttpContext httpContext,
            ICommandHandler<AddExperienceCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse(request.Type, ignoreCase: true, out ExperienceType type))
                return Results.Problem(detail: "Invalid experience type.", statusCode: StatusCodes.Status400BadRequest);

            var result = await handler.Handle(new AddExperienceCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                type,
                request.Organization,
                request.Position,
                request.Start,
                request.End,
                request.Summary), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/educations", async (
            Guid id,
            AddEducationRequest request,
            HttpContext httpContext,
            ICommandHandler<AddEducationCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new AddEducationCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Institution,
                request.Degree,
                request.FieldOfStudy,
                request.Start,
                request.End,
                request.Grade), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/certificates", async (
            Guid id,
            AddCertificateRequest request,
            HttpContext httpContext,
            ICommandHandler<AddCertificateCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new AddCertificateCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Name,
                request.Issuer,
                request.CredentialId,
                request.CredentialUrl,
                request.ValidityStart,
                request.ValidityEnd), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/projects", async (
            Guid id,
            AddProjectRequest request,
            HttpContext httpContext,
            ICommandHandler<AddProjectCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new AddProjectCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Name,
                request.Start,
                request.End,
                request.Description,
                request.RepositoryUrl,
                request.LiveDemoUrl,
                request.Technologies,
                request.Highlights), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/languages", async (
            Guid id,
            AddLanguageRequest request,
            HttpContext httpContext,
            ICommandHandler<AddLanguageCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new AddLanguageCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Name,
                request.Fluency), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/awards", async (
            Guid id,
            AddAwardRequest request,
            HttpContext httpContext,
            ICommandHandler<AddAwardCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new AddAwardCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Title,
                request.Awarder,
                request.Date,
                request.Summary), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/publications", async (
            Guid id,
            AddPublicationRequest request,
            HttpContext httpContext,
            ICommandHandler<AddPublicationCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new AddPublicationCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Title,
                request.Publisher,
                request.Url,
                request.ReleaseDate,
                request.Summary), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/interests", async (
            Guid id,
            AddInterestRequest request,
            HttpContext httpContext,
            ICommandHandler<AddInterestCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new AddInterestCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Name,
                request.Keywords), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{id:guid}/references", async (
            Guid id,
            AddReferenceRequest request,
            HttpContext httpContext,
            ICommandHandler<AddReferenceCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new AddReferenceCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Name,
                request.Position,
                request.Company,
                request.Email,
                request.PhoneNumber,
                request.ReferenceText), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            ICommandHandler<DeleteResumeCommand, Result<ResumeId>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new DeleteResumeCommand(httpContext.User.GetAccountId(), new ResumeId(id)), cancellationToken);
            return result.ToHttpResult(_ => Results.NoContent());
        });

        return group;
    }
}

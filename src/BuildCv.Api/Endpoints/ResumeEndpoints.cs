using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Resumes;
using BuildCv.Application.Scoring;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

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

        // One whole CV in one request, in place of POST /resumes plus up to fifteen per-section calls.
        // It is the endpoint a HUMAN REVIEW SCREEN posts to: extraction reaches roughly 65% field
        // accuracy on real CVs, so the corrected draft is what reaches the domain, never the raw
        // extraction.
        //
        // NO ENUM PARSING HERE, unlike the four routes below that do it in the lambda. Every enum in a
        // draft is parsed by ResumeDraftValidator instead, so a bad level comes back as a FIELD ERROR
        // beside whatever else is wrong rather than as a bare 400 that names nothing. Copying the
        // endpoint guard into a second place would be one rule stated twice, and the two would drift.
        //
        // 201 with the aggregate, which is the same body POST /resumes already answers. A second
        // response shape for one aggregate is how a client ends up with two models of a resume.
        group.MapPost("/import", async Task<IResult> (
            ImportResumeRequest request,
            ICommandHandler<CreateResumeFromDraftCommand, ResumeImportResult> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new CreateResumeFromDraftCommand(httpContext.User.GetAccountId(), request.ToDraft()),
                cancellationToken);

            return result.IsSuccess
                ? Results.Created($"/resumes/{result.Resume!.Id.Value}", result.Resume)
                : result.FieldErrors.ToValidationProblem();
        })
        .WithSummary("Creates a complete resume from one reviewed draft.")
        .WithDescription(
            "Every field is sent as a STRING, including dates (yyyy-MM-dd), numbers and levels, so that "
            + "nothing can fail at model binding before the draft has been validated as a whole. "
            + "Validation is all-or-nothing and collects EVERY bad field in one pass: a rejected draft "
            + "answers 400 with the standard ProblemDetails `errors` object, keyed by JSON field path "
            + "(`experiences[2].end`, `contact.phoneNumber`), and creates nothing. Levels accept the "
            + "enum name or its number. Duplicate skills, certificates, languages and interests are "
            + "reported against the LATER occurrence — that is the line to delete.");

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

        // Score history hangs off the CV that owns it, not off /scoring, for the same reason
        // /{id}/skills does: it is part of this resource, and the ownership check is the one every
        // handler in this file already makes.
        //
        // OLDEST FIRST — the single exception to this repo's newest-first paging convention, and the
        // reason it exists is the product: a candidate reads a history forwards, from the first run
        // through what each edit changed. Walking it with `cursor` therefore moves FORWARD IN TIME.
        group.MapGet("/{id:guid}/analyses", async (
            Guid id,
            HttpContext httpContext,
            IQueryHandler<GetAnalysisHistoryQuery, Result<Page<Analysis>>> handler,
            CancellationToken cancellationToken,
            int? limit,
            string? cursor) =>
        {
            var result = await handler.Handle(new GetAnalysisHistoryQuery(
                httpContext.User.GetAccountId(), new ResumeId(id), limit, cursor), cancellationToken);

            // Mapped through the same AnalysisResponse the score endpoint returns. Each entry is
            // therefore identical in shape to what /scoring/score answered when it was created,
            // recommendations included and in the same order — which is what makes "did my edit help"
            // a comparison a client can just do.
            return result.ToHttpResult(page => Results.Ok(new PagedResponse<AnalysisResponse>(
                [.. page.Items.Select(AnalysisResponse.From)], page.NextCursor)));
        })
        .WithSummary("Returns this resume's score history, OLDEST FIRST, keyset paginated.")
        .WithDescription(
            "The one list in this API that pages oldest first: a score history is read forwards, so "
            + "`cursor` walks toward the present. Entries are the same shape /scoring/score returns. "
            + "A section whose `breakdown.weights.<section>` is 0 was not asked about by the posting "
            + "that entry was scored against — it neither helped nor hurt, and the remaining weights are "
            + "renormalized to still total 1.0. Two entries can both report `schemaVersion` 2 and still "
            + "have been scored under different weightings, because each posting asks about a different "
            + "set of sections; compare `weights` before comparing `overallScore`.");

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

        group.MapPost("/{id:guid}/educations", async Task<IResult> (
            Guid id,
            AddEducationRequest request,
            HttpContext httpContext,
            ICommandHandler<AddEducationCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            // IsDefined for the same reason as the languages endpoint below: TryParse accepts any
            // numeric string and the tinyint conversion is unchecked, so "-1" would land as 255 —
            // above Doctorate — instead of being refused.
            EducationLevel? level = null;
            if (request.Level is not null)
            {
                if (!Enum.TryParse(request.Level, ignoreCase: true, out EducationLevel parsed)
                    || !Enum.IsDefined(parsed))
                    return Results.Problem(detail: "Invalid education level.", statusCode: StatusCodes.Status400BadRequest);
                level = parsed;
            }

            var result = await handler.Handle(new AddEducationCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Institution,
                request.Degree,
                request.FieldOfStudy,
                request.Start,
                request.End,
                request.Grade,
                level), cancellationToken);
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

        // Level is parsed here and rejected with a 400 BEFORE the handler runs, matching how
        // AddSkillRequest.Level is already handled. Fluency is passed straight through untouched:
        // nothing in this pipeline may derive a Level from it.
        //
        // IsDefined is not belt-and-braces on top of TryParse — TryParse ACCEPTS any numeric string,
        // and the column is tinyint mapped with an unchecked Expression.Convert. Measured against
        // SQL Server: "99" stores as 99, "300" truncates to 44, and "-1" WRAPS TO 255 — silently, with
        // no exception and no log. 255 is above Native, so the most obviously-invalid input a fuzzer
        // sends becomes maximum proficiency, and PR 3's `held >= required` then tells the candidate
        // they meet a requirement they do not. IsDefined runs on the CLR value before that conversion,
        // which is what closes all three.
        //
        // It must stay IsDefined rather than "reject numeric input": GET returns level as a NUMBER
        // (no JsonStringEnumConverter is configured), so a read-modify-write client legitimately POSTs
        // 4 back. Valid numbers keep working; only undefined ones do not.
        //
        // What this does NOT do is narrow the input space to the enum's own names. TryParse
        // OR-combines COMMA-SEPARATED members whether or not the type is [Flags], and the result is
        // usually still a defined member: measured, "Conversational,Professional" parses to 1|2 = 3 and
        // comes back as Fluent — higher than either name sent — with IsDefined returning true. The
        // guard bounds it (the stored value is always a real member, so PR 3 can never read "above
        // Native", which was the actual danger) but does not close it. It exists identically on the
        // four pre-existing parse sites and belongs in the same follow-up.
        group.MapPost("/{id:guid}/languages", async Task<IResult> (
            Guid id,
            AddLanguageRequest request,
            HttpContext httpContext,
            ICommandHandler<AddLanguageCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            LanguageProficiency? level = null;
            if (request.Level is not null)
            {
                if (!Enum.TryParse(request.Level, ignoreCase: true, out LanguageProficiency parsed)
                    || !Enum.IsDefined(parsed))
                    return Results.Problem(detail: "Invalid language proficiency.", statusCode: StatusCodes.Status400BadRequest);
                level = parsed;
            }

            var result = await handler.Handle(new AddLanguageCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.Name,
                request.Fluency,
                level), cancellationToken);
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

using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;
using CandidateCommands = BuildCv.Application.Candidates;

namespace BuildCv.Api.Endpoints;

// The candidate profile is the data a CV is generated FROM, and it is edited like a CV is: ten
// collections, each with the same POST-append / PUT-replace / DELETE-remove trio. The surface is a
// faithful echo of ResumeEndpoints, MINUS the {id} segment — a profile is the caller's own and there
// is only ever one, so nothing in a route needs to name it. The item routes deliberately reuse the
// resume's ItemSections table so the two surfaces cannot drift on what a segment names.
//
// The one structural difference from the resume surface: the resume routes live under /resumes/{id},
// so they answer everything with ResumeSummaryResponse and the full CV only on GET /{id}. The profile
// routes are the same — the write responses are summaries (they carry counts, not entries), and the
// ids that address entries exist only on GET /v1/profile.
public static class CandidateProfileEndpoints
{
    public static RouteGroupBuilder MapCandidateProfileEndpoints(this IEndpointRouteBuilder app, string prefix = "profile")
    {
        var group = app.MapGroup($"/{prefix}")
            .WithTags("Candidate Profile")
            .RequireAuthorization(AuthorizationPolicies.Candidate);

        group.MapGet("/", async (
            HttpContext httpContext,
            IQueryHandler<CandidateCommands.GetCandidateProfileQuery, Result<CandidateProfileWithItemIds>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new CandidateCommands.GetCandidateProfileQuery(httpContext.User.GetAccountId()), cancellationToken);
            return result.ToHttpResult(loaded => Results.Ok(CandidateProfileResponse.From(loaded)));
        })
        .Produces<CandidateProfileResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary("Returns the caller's candidate profile in full.")
        .WithDescription(
            "The candidate profile — the data a CV is generated from — with every entry of every "
            + "collection and the id that addresses each one. 404 until the caller's first "
            + "PUT /v1/profile/contact creates the profile.");

        group.MapPut("/contact", async (
            UpdateContactRequest request,
            ICommandHandler<CandidateCommands.UpsertProfileContactCommand, Result<CandidateProfile>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new CandidateCommands.UpsertProfileContactCommand(
                httpContext.User.GetAccountId(),
                request.FullName,
                request.Email,
                request.PhoneNumber,
                request.Location,
                request.Summary), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        })
        .Produces<CandidateProfileSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary("Creates the caller's profile if none exists, then sets its contact.")
        .WithDescription(
            "An upsert: the first call creates the profile so the item routes have a profile to write "
            + "into; later calls update the contact. The response is the summary — counts, not entries "
            + "— so fetch GET /v1/profile when the ids that address entries matter.");

        MapItemWrites(group);
        MapItemDeletes(group);

        return group;
    }

    /// <summary>
    /// Registers the two ways an entry gets into one of a profile's ten collections: <c>POST /{segment}</c>
    /// appends one, <c>PUT /{segment}/{itemId}</c> replaces one.
    /// </summary>
    /// <remarks>
    /// The same ONE DELEGATE FOR BOTH VERBS shape as the resume surface, and the same reason: four of
    /// these collections refuse an out-of-range enum before the handler runs, and those guards are the
    /// most consequential lines in this file — an undefined <c>LanguageProficiency</c> wraps to 255 in
    /// the tinyint column and reads as above Native. A separate PUT lambda would be a second copy of
    /// each guard, and a copy that drifts is a route where it quietly does not apply.
    /// </remarks>
    private static void MapItemWrites(RouteGroupBuilder group)
    {
        MapItemWrites<AddSkillRequest, CandidateCommands.AddSkillCommand>(group, "skills", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            // IsDefined for the reason spelled out in full on the resume languages endpoint: TryParse
            // accepts ANY numeric string, and the level is mapped to tinyint with an unchecked
            // conversion, so "99" would be stored, not refused.
            SkillLevel? level = null;
            if (request.Level is not null)
            {
                if (!Enum.TryParse(request.Level, ignoreCase: true, out SkillLevel parsed)
                    || !Enum.IsDefined(parsed))
                    return Results.Problem(detail: "Invalid skill level.", statusCode: StatusCodes.Status400BadRequest);
                level = parsed;
            }

            var result = await handler.Handle(new CandidateCommands.AddSkillCommand(
                httpContext.User.GetAccountId(),
                request.SkillName,
                level,
                request.YearsOfExperience,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });

        MapItemWrites<AddExperienceRequest, CandidateCommands.AddExperienceCommand>(group, "experiences", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            if (!Enum.TryParse(request.Type, ignoreCase: true, out ExperienceType type)
                || !Enum.IsDefined(type))
                return Results.Problem(detail: "Invalid experience type.", statusCode: StatusCodes.Status400BadRequest);

            var result = await handler.Handle(new CandidateCommands.AddExperienceCommand(
                httpContext.User.GetAccountId(),
                type,
                request.Organization,
                request.Position,
                request.Start,
                request.End,
                request.Summary,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });

        MapItemWrites<AddEducationRequest, CandidateCommands.AddEducationCommand>(group, "educations", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            EducationLevel? level = null;
            if (request.Level is not null)
            {
                if (!Enum.TryParse(request.Level, ignoreCase: true, out EducationLevel parsed)
                    || !Enum.IsDefined(parsed))
                    return Results.Problem(detail: "Invalid education level.", statusCode: StatusCodes.Status400BadRequest);
                level = parsed;
            }

            var result = await handler.Handle(new CandidateCommands.AddEducationCommand(
                httpContext.User.GetAccountId(),
                request.Institution,
                request.Degree,
                request.FieldOfStudy,
                request.Start,
                request.End,
                request.Grade,
                level,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });

        MapItemWrites<AddCertificateRequest, CandidateCommands.AddCertificateCommand>(group, "certificates", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            var result = await handler.Handle(new CandidateCommands.AddCertificateCommand(
                httpContext.User.GetAccountId(),
                request.Name,
                request.Issuer,
                request.CredentialId,
                request.CredentialUrl,
                request.ValidityStart,
                request.ValidityEnd,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });

        MapItemWrites<AddProjectRequest, CandidateCommands.AddProjectCommand>(group, "projects", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            var result = await handler.Handle(new CandidateCommands.AddProjectCommand(
                httpContext.User.GetAccountId(),
                request.Name,
                request.Start,
                request.End,
                request.Description,
                request.RepositoryUrl,
                request.LiveDemoUrl,
                request.Technologies,
                request.Highlights,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });

        MapItemWrites<AddLanguageRequest, CandidateCommands.AddLanguageCommand>(group, "languages", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            LanguageProficiency? level = null;
            if (request.Level is not null)
            {
                if (!Enum.TryParse(request.Level, ignoreCase: true, out LanguageProficiency parsed)
                    || !Enum.IsDefined(parsed))
                    return Results.Problem(detail: "Invalid language proficiency.", statusCode: StatusCodes.Status400BadRequest);
                level = parsed;
            }

            var result = await handler.Handle(new CandidateCommands.AddLanguageCommand(
                httpContext.User.GetAccountId(),
                request.Name,
                request.Fluency,
                level,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });

        MapItemWrites<AddAwardRequest, CandidateCommands.AddAwardCommand>(group, "awards", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            var result = await handler.Handle(new CandidateCommands.AddAwardCommand(
                httpContext.User.GetAccountId(),
                request.Title,
                request.Awarder,
                request.Date,
                request.Summary,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });

        MapItemWrites<AddPublicationRequest, CandidateCommands.AddPublicationCommand>(group, "publications", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            var result = await handler.Handle(new CandidateCommands.AddPublicationCommand(
                httpContext.User.GetAccountId(),
                request.Title,
                request.Publisher,
                request.Url,
                request.ReleaseDate,
                request.Summary,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });

        MapItemWrites<AddInterestRequest, CandidateCommands.AddInterestCommand>(group, "interests", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            var result = await handler.Handle(new CandidateCommands.AddInterestCommand(
                httpContext.User.GetAccountId(),
                request.Name,
                request.Keywords,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });

        MapItemWrites<AddReferenceRequest, CandidateCommands.AddReferenceCommand>(group, "references", async (
            replacingItemId, request, httpContext, handler, cancellationToken) =>
        {
            var result = await handler.Handle(new CandidateCommands.AddReferenceCommand(
                httpContext.User.GetAccountId(),
                request.Name,
                request.Position,
                request.Company,
                request.Email,
                request.PhoneNumber,
                request.ReferenceText,
                replacingItemId), cancellationToken);
            return result.ToHttpResult(profile => Results.Ok(CandidateProfileSummaryResponse.From(profile)));
        });
    }

    private static void MapItemWrites<TRequest, TCommand>(
        RouteGroupBuilder group,
        string segment,
        Func<int?, TRequest, HttpContext, ICommandHandler<TCommand, Result<CandidateProfile>>, CancellationToken, Task<IResult>> write)
        where TCommand : ICommand<Result<CandidateProfile>>
    {
        group.MapPost($"/{segment}", (
            TRequest request,
            HttpContext httpContext,
            ICommandHandler<TCommand, Result<CandidateProfile>> handler,
            CancellationToken cancellationToken) =>
                write(null, request, httpContext, handler, cancellationToken))
        .Produces<CandidateProfileSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary($"Appends an entry to the profile's {segment}.")
        .WithDescription(
            $"Appends a {segment} entry to the caller's profile. 404 when the profile does not exist "
            + "yet — the first PUT /v1/profile/contact creates it. The response is the summary; the "
            + "id that addresses this entry exists on GET /v1/profile.");

        group.MapPut($"/{segment}/{{itemId:int}}", (
            int itemId,
            TRequest request,
            HttpContext httpContext,
            ICommandHandler<TCommand, Result<CandidateProfile>> handler,
            CancellationToken cancellationToken) =>
                write(itemId, request, httpContext, handler, cancellationToken))
        .Produces<CandidateProfileSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary($"Replaces one entry of the profile's {segment}.")
        .WithDescription(
            "The body is the same as the POST that appends: this REPLACES the entry outright rather "
            + "than patching the fields you send, so omitting one clears it. `itemId` is the `id` that "
            + "entry carries in `GET /v1/profile` — not its position in the array — and one that names "
            + "no entry of the profile answers 404. The replacement is a NEW entry with a NEW id; "
            + "re-fetch the profile before addressing it again.\n"
            + "\n"
            + "The profile's Add is idempotent, so a replacement whose value already exists elsewhere "
            + "in the collection is absorbed rather than duplicated: the named entry is removed and the "
            + "existing equal entry stays, shrinking the collection by one.");
    }

    // Ten routes, one handler, the same reasoning as the resume deletes: everything a per-collection
    // copy could get wrong lives in RemoveProfileItemHandler and is written once.
    //
    // 204, not the profile. A delete has nothing to describe, and returning the profile would invite a
    // client to re-render from a body whose entry ids it must re-read anyway. Re-fetch GET /v1/profile
    // when the ids matter.
    private static void MapItemDeletes(RouteGroupBuilder group)
    {
        foreach (var (segment, section) in ItemSections.All)
        {
            group.MapDelete($"/{segment}/{{itemId:int}}", async (
                int itemId,
                HttpContext httpContext,
                ICommandHandler<CandidateCommands.RemoveProfileItemCommand, Result<CandidateProfile>> handler,
                CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(
                    new CandidateCommands.RemoveProfileItemCommand(httpContext.User.GetAccountId(), section, itemId),
                    cancellationToken);

                return result.ToHttpResult(_ => Results.NoContent());
            })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesResultProblems()
            .ProducesAuthProblems()
            .WithSummary($"Removes one entry from the profile's {segment}.")
            .WithDescription(
                "`itemId` is the `id` that entry carries in `GET /v1/profile` — the only route that "
                + "hands them out. It is NOT the entry's position in the array. An id that names no "
                + "entry of the profile answers 404. Answers 204 with no body; re-fetch the profile "
                + "when you need the ids again, because an id is not reused after its entry is removed.");
        }
    }
}

using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Resumes;
using BuildCv.Application.Scoring;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using Microsoft.AspNetCore.Mvc;

namespace BuildCv.Api.Endpoints;

public static class ResumeEndpoints
{
    // Exposed so a test can send exactly one byte over it rather than restating the number.
    public const long ImportRequestSizeLimitBytes = 2 * 1024 * 1024;

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
            return result.ToHttpResult(resume => Results.Created($"/v1/resumes/{resume.Id.Value}", resume));
        });

        // One whole CV in one request, in place of POST /v1/resumes plus up to fifteen per-section calls.
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
            ResumeImportRateLimiter rateLimiter,
            ILogger<Program> logger,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var accountId = httpContext.User.GetAccountId();

            // Per account, not per IP, and acquired here rather than as a named policy — see
            // ResumeImportRateLimiter. An accepted import is the most durable write in this API, and
            // the global 100/min per-IP limiter was the only thing bounding it.
            using var lease = await rateLimiter.AcquireAsync(accountId, cancellationToken);
            if (!lease.IsAcquired)
            {
                RateLimitResponse.SetRetryAfter(httpContext.Response, lease);
                AuditLog.Log(logger, "resume_import_throttled", accountId, httpContext);
                return Results.Problem(
                    detail: "Too many resume imports.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var result = await handler.Handle(
                new CreateResumeFromDraftCommand(accountId, request.ToDraft()),
                cancellationToken);

            return result.IsSuccess
                ? Results.Created($"/v1/resumes/{result.Resume!.Id.Value}", result.Resume)
                : result.FieldErrors.ToValidationProblem();
        })
        // THE ONLY REQUEST-SIZE LIMIT IN THIS API, and the first endpoint that needed one. Kestrel's
        // default is 30,000,000 bytes (~28.6 MB) and nothing here changes it, so without this a 28 MB
        // body of `{"projects":[{},{},...]}` was fully deserialized before ResumeDraftLimits could
        // decline it.
        //
        // THE FRAMEWORK ENFORCES THIS ON ITS OWN. Kestrel applies IRequestSizeLimitMetadata while the
        // body is READ, which is why a handler that never touches its body is never refused for the
        // size of one — and measuring exactly that is how an earlier revision of this file talked
        // itself into a middleware nothing needed. Measured properly, on a real Kestrel host against
        // an endpoint that binds a body: under the limit 200, over it 413, and chunked with no
        // Content-Length also 413. ResumeImportSizeLimitTests pins that behaviour so the claim is
        // executed rather than asserted.
        //
        // The 413 comes back with Content-Length: 0 and Connection: close, and no IExceptionHandler
        // runs, so it is the one error in this API that is not ProblemDetails-shaped. That cannot be
        // fixed from inside the app — see MalformedRequestExceptionHandler, which covers the malformed
        // bodies that CAN be shaped and explains why this one cannot.
        //
        // 2 MiB. Arithmetic, not a round number: a draft filled to every cap is roughly 700 KB of JSON —
        // 50 experiences at ~5 KB each once their 50 highlights are counted, 50 projects at ~6.5 KB with
        // technologies and highlights, 200 publications at ~400 bytes, and the rest well under that. 2
        // MiB is about three times the largest draft the caps can admit and one fifteenth of the
        // framework default.
        .WithMetadata(new RequestSizeLimitAttribute(ImportRequestSizeLimitBytes))
        .WithSummary("Creates a complete resume from one reviewed draft.")
        .WithDescription(
            "Every field is sent as a STRING, including dates (yyyy-MM-dd), numbers and levels, so that no "
            + "VALUE can be rejected at model binding: a malformed date or an unknown level comes back as "
            + "a field error rather than as a framework 400 naming nothing. Malformed JSON, a null body "
            + "and a body over 2 MiB are still refused by the server before validation runs. "
            + "Validation is all-or-nothing and collects EVERY bad field in one pass: a rejected draft "
            + "answers 400 with the standard ProblemDetails `errors` object, keyed by JSON field path "
            + "(`experiences[2].end`, `contact.phoneNumber`), and creates nothing. A null array element "
            + "is reported at its own index. Levels accept the enum name or its number. Duplicate skills, "
            + "certificates, languages and interests are reported against the LATER occurrence — that is "
            + "the line to delete — including when that item has another bad field as well.");

        // The upload half of the import flow: a PDF, DOCX or plain-text file in, its raw text back.
        // Raw text ONLY — no section detection and no draft: the candidate pastes or corrects the text
        // into the review screen, and POST /v1/resumes/import is what creates anything. That split is
        // deliberate: extraction is mechanical and provable, section detection is heuristic, and this
        // endpoint stays the permanent fallback for every CV the heuristics cannot read.
        group.MapPost("/import/extract", async Task<IResult> (
            IFormFile file,
            ICommandHandler<ExtractDocumentTextCommand, Result<DocumentExtraction>> handler,
            DocumentExtractionRateLimiter rateLimiter,
            ILogger<Program> logger,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var accountId = httpContext.User.GetAccountId();

            // This throttles PARSING, not INGEST, and the distinction is deliberate. IFormFile binding
            // has already run by the time this lambda executes — the body is read and (past 64 KB)
            // spilled to a temp file before this line — so acquiring the lease here cannot refuse a
            // request before its bytes arrive. It does not need to: ingest is bounded per-upload by the
            // 5 MiB request-size ceiling below and per-source by the global 100/min limiter, and neither
            // depends on the principal. What this limiter bounds is the expensive half — synchronous
            // parsing, the most CPU- and memory-heavy work in the API — keyed to the account, which is
            // the right unit because the caller is always authenticated. See DocumentExtractionRateLimiter.
            using var lease = await rateLimiter.AcquireAsync(accountId, cancellationToken);
            if (!lease.IsAcquired)
            {
                RateLimitResponse.SetRetryAfter(httpContext.Response, lease);
                AuditLog.Log(logger, "resume_extract_throttled", accountId, httpContext);
                return Results.Problem(
                    detail: "Too many document extractions.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            await using var content = file.OpenReadStream();
            var result = await handler.Handle(
                new ExtractDocumentTextCommand(content, file.ContentType),
                cancellationToken);

            return result.ToHttpResult(extraction => Results.Ok(new ExtractDocumentTextResponse(
                extraction.Text, extraction.PageCount, extraction.Warnings)));
        })
        // The extraction ceiling, enforced by Kestrel exactly like the 2 MiB one above — see that
        // endpoint's comment for why the framework enforces this metadata on its own and why the 413 is
        // not ProblemDetails-shaped. The number is the port's own constant so the HTTP ceiling and the
        // extractor's bound cannot drift; why 5 MiB is argued on IDocumentTextExtractor. Multipart
        // framing counts against it, so the file itself must fit with a few hundred bytes to spare.
        // FormOptions.MultipartBodyLengthLimit also applies to a form body, but at its 128 MiB default
        // it sits far above this ceiling and never fires first — ResumeExtractSizeLimitTests measures
        // both limits against real Kestrel rather than asserting this from documentation.
        .WithMetadata(new RequestSizeLimitAttribute(IDocumentTextExtractor.MaxDocumentBytes))
        // NOT an opt-out of CSRF protection — an opt-out of the FRAMEWORK's second, unusable copy of
        // it. Binding IFormFile makes minimal APIs stamp the endpoint with required-antiforgery
        // metadata, and that requires app.UseAntiforgery() in the pipeline: measured without this
        // line, every request here — bearer ones included — answered a 500 InvalidOperationException
        // naming the missing middleware. This pipeline deliberately has no UseAntiforgery; its CSRF
        // control is CsrfGuardMiddleware, which covers this route like every other cookie-
        // authenticated unsafe method (both directions pinned in ResumeExtractTests) and which knows
        // this API's contract — bearer requests carry no ambient credential and are exempt by design,
        // something the framework validator would refuse. Do not add this route to
        // CsrfGuardMiddleware.ExemptPaths.
        .DisableAntiforgery()
        .WithSummary("Extracts the raw text of an uploaded CV document.")
        .WithDescription(
            "Multipart upload with one `file` part: PDF, DOCX or plain text, at most 5 MiB for the "
            + "whole request. Answers the extracted raw text, the page count when the format states "
            + "one (only PDF does), and warnings — a PDF with no text layer (a scan) is reported as "
            + "exactly that rather than as an empty document; OCR is not supported. The declared "
            + "content type selects the parser and the file's leading bytes must agree with it. "
            + "Nothing is stored: review and correct the text, then send the draft to POST "
            + "/v1/resumes/import.");

        // The quality-of-life step: a document in, a POPULATED draft out — the same text as /extract, run
        // through the heuristic parser so the candidate corrects a pre-filled form instead of typing it.
        //
        // NOTHING IS CREATED HERE. This proposes a draft and its confidence; the only writer in the flow
        // is POST /v1/resumes/import, which takes the draft the candidate CONFIRMED. The handler has no
        // repository to persist with (pinned in ProposeResumeDraftFromDocumentHandlerTests), and this test
        // suite pins that a call here creates no resume — there is no "extract and save" shortcut.
        //
        // The response carries the draft (an ImportResumeRequest, ready to post straight back) AND a
        // SEPARATE confidence structure. Confidence is advice to the review screen and never crosses back:
        // rule-based extraction reaches ~65% field accuracy on real CVs, so a field the parser is unsure
        // about arrives empty and FLAGGED, and a two-column layout — the dominant failure — is warned
        // about, never silently interleaved.
        group.MapPost("/import/propose", async Task<IResult> (
            IFormFile file,
            ICommandHandler<ProposeResumeDraftFromDocumentCommand, Result<ResumeDraftProposal>> handler,
            DocumentExtractionRateLimiter rateLimiter,
            ILogger<Program> logger,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var accountId = httpContext.User.GetAccountId();

            // Shares the per-account document-parsing budget with /extract (see
            // DocumentExtractionRateLimiter): both parse an uploaded document synchronously, and this one
            // parses the PDF twice — once for text, once for the column geometry — so it belongs under the
            // same ceiling rather than a looser one.
            using var lease = await rateLimiter.AcquireAsync(accountId, cancellationToken);
            if (!lease.IsAcquired)
            {
                RateLimitResponse.SetRetryAfter(httpContext.Response, lease);
                AuditLog.Log(logger, "resume_propose_throttled", accountId, httpContext);
                return Results.Problem(
                    detail: "Too many document extractions.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            await using var content = file.OpenReadStream();
            var result = await handler.Handle(
                new ProposeResumeDraftFromDocumentCommand(content, file.ContentType),
                cancellationToken);

            return result.ToHttpResult(proposal => Results.Ok(ProposeResumeDraftResponse.FromProposal(proposal)));
        })
        // Same 5 MiB ceiling and CSRF story as /extract above: the ceiling is the extractor's own
        // constant (they cannot drift), and .DisableAntiforgery removes the framework's second, unusable
        // antiforgery check that binding IFormFile stamps on — CsrfGuardMiddleware still covers this route.
        // Do not add it to CsrfGuardMiddleware.ExemptPaths.
        .WithMetadata(new RequestSizeLimitAttribute(IDocumentTextExtractor.MaxDocumentBytes))
        .DisableAntiforgery()
        .WithSummary("Proposes a best-effort resume draft from an uploaded CV document.")
        .WithDescription(
            "Multipart upload with one `file` part: PDF, DOCX or plain text, at most 5 MiB. Answers a "
            + "populated draft — the same shape POST /v1/resumes/import accepts — and a SEPARATE confidence "
            + "structure the review screen uses and does NOT post back. Extraction is best-effort: a field "
            + "the parser could not read confidently is left empty and flagged (confidence "
            + "`NotExtracted`), never guessed; levels, experience type and end dates are never invented; "
            + "and a two-column layout is warned about rather than silently reordered. Nothing is stored — "
            + "correct the draft, then submit it to POST /v1/resumes/import, the only endpoint that creates "
            + "a resume.");

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

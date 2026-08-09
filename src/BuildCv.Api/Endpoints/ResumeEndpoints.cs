using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Observability;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Readability;
using BuildCv.Application.Resumes;
using BuildCv.Application.Scoring;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Readability;
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
            return result.ToHttpResult(resume =>
                Results.Created($"/v1/resumes/{resume.Id.Value}", ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status201Created)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
        // 201 with a ResumeResponse, which is the same body POST /v1/resumes answers. A second
        // response shape for one aggregate is how a client ends up with two models of a resume.
        group.MapPost("/import", async Task<IResult> (
            ImportResumeRequest request,
            ICommandHandler<CreateResumeFromDraftCommand, ResumeImportResult> handler,
            ResumeImportRateLimiter rateLimiter,
            BuildCvMetrics metrics,
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
                // Counted here rather than in the RateLimiter middleware's OnRejected, which never runs
                // for this limiter: it is acquired inside the endpoint (see ResumeImportRateLimiter), so
                // the middleware has already let the request through by the time it is refused.
                metrics.ThrottleRejection(ThrottlePolicies.ResumeImport);
                return Results.Problem(
                    detail: "Too many resume imports.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var result = await handler.Handle(
                new CreateResumeFromDraftCommand(accountId, request.ToDraft(), request.ImportEvidence),
                cancellationToken);

            return result.IsSuccess
                ? Results.Created(
                    $"/v1/resumes/{result.Resume!.Id.Value}", ResumeSummaryResponse.From(result.Resume))
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
        .Produces<ResumeSummaryResponse>(StatusCodes.Status201Created)
        // The field-error shape: this route collects EVERY bad field in one pass and keys them by path
        // (`experiences[2].endDate`), which is the only thing a forty-field review screen can attach to
        // the right input.
        .ProducesValidationProblem()
        .ProducesAuthProblems()
        .WithSummary("Creates a complete resume from one reviewed draft.")
        .WithDescription(
            "Every field is sent as a STRING, including dates, numbers and levels, so that no "
            + "VALUE can be rejected at model binding: a malformed date or an unknown level comes back as "
            + "a field error rather than as a framework 400 naming nothing. Malformed JSON, a null body "
            + "and a body over 2 MiB are still refused by the server before validation runs. "
            + "Validation is all-or-nothing and collects EVERY bad field in one pass: a rejected draft "
            + "answers 400 with the standard ProblemDetails `errors` object, keyed by JSON field path "
            + "(`experiences[2].end`, `contact.phoneNumber`), and creates nothing. A null array element "
            + "is reported at its own index. Levels accept the enum name or its number. Duplicate skills, "
            + "certificates, languages and interests are reported against the LATER occurrence — that is "
            + "the line to delete — including when that item has another bad field as well. "
            + "PERIOD DATES CARRY THE PRECISION YOU HAVE: the `start` and `end` of an experience, an "
            + "education, a project and a certificate's validity accept `yyyy-MM-dd`, `yyyy-MM` or "
            + "`yyyy`, so a CV that says \"June 2015\" needs no invented day and comes back as `2015-06` "
            + "wherever it is read. A date you state fully stays fully precise — nothing is widened. The "
            + "single-day fields (`awards[].date`, `publications[].releaseDate`) are still `yyyy-MM-dd`. "
            + "`importEvidence` is the opaque token POST /v1/resumes/import/propose returned inside the "
            + "draft it proposed: send it back UNCHANGED to have the readability engine grade the "
            + "document you uploaded, or omit it entirely — a draft typed by hand needs none, and its "
            + "ATS-parseability section is then renormalized out rather than scored zero. A token that "
            + "is malformed, was issued to another account, or is older than two hours is reported as a "
            + "field error at `importEvidence` alongside any other bad field, and nothing is created; "
            + "resubmitting without it succeeds. The token describes the DOCUMENT it was minted for, not "
            + "the draft you send with it: nothing stops you posting it beside a different resume of "
            + "your own, and the signals will then describe a file that resume did not come from.");

        // The upload half of the import flow: a PDF, DOCX or plain-text file in, its raw text back.
        // Raw text ONLY — no section detection and no draft: the candidate pastes or corrects the text
        // into the review screen, and POST /v1/resumes/import is what creates anything. That split is
        // deliberate: extraction is mechanical and provable, section detection is heuristic, and this
        // endpoint stays the permanent fallback for every CV the heuristics cannot read.
        group.MapPost("/import/extract", async Task<IResult> (
            IFormFile file,
            ICommandHandler<ExtractDocumentTextCommand, Result<DocumentExtraction>> handler,
            DocumentExtractionRateLimiter rateLimiter,
            BuildCvMetrics metrics,
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
                metrics.ThrottleRejection(ThrottlePolicies.DocumentExtraction);
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
        .Produces<ExtractDocumentTextResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
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
            IImportEvidenceProtector importEvidenceProtector,
            BuildCvMetrics metrics,
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
                metrics.ThrottleRejection(ThrottlePolicies.DocumentExtraction);
                return Results.Problem(
                    detail: "Too many document extractions.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            await using var content = file.OpenReadStream();
            var result = await handler.Handle(
                new ProposeResumeDraftFromDocumentCommand(content, file.ContentType),
                cancellationToken);

            // SIGNED HERE, at the composition root, and not in the handler. The handler depends on
            // exactly the two read-only extraction ports and nothing else — that is the Application half
            // of "extraction persists nothing", pinned by a test that reads its constructor — so a
            // service holding a key does not belong in it. The account is also only knowable here.
            //
            // Signals are null only for a proposal that came from the parser with no document behind it,
            // which this route cannot produce; the handler always sets them, and
            // Propose_EveryProposal_CarriesTheSignalsOfTheDocumentItRead is what keeps that true.
            return result.ToHttpResult(proposal => Results.Ok(ProposeResumeDraftResponse.FromProposal(
                proposal,
                proposal.Signals is { } signals
                    ? importEvidenceProtector.Protect(signals, accountId)
                    : null)));
        })
        // Same 5 MiB ceiling and CSRF story as /extract above: the ceiling is the extractor's own
        // constant (they cannot drift), and .DisableAntiforgery removes the framework's second, unusable
        // antiforgery check that binding IFormFile stamps on — CsrfGuardMiddleware still covers this route.
        // Do not add it to CsrfGuardMiddleware.ExemptPaths.
        .WithMetadata(new RequestSizeLimitAttribute(IDocumentTextExtractor.MaxDocumentBytes))
        .DisableAntiforgery()
        .Produces<ProposeResumeDraftResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary("Proposes a best-effort resume draft from an uploaded CV document.")
        .WithDescription(
            "Multipart upload with one `file` part: PDF, DOCX or plain text, at most 5 MiB. Answers a "
            + "populated draft — the same shape POST /v1/resumes/import accepts — and a SEPARATE confidence "
            + "structure the review screen uses and does NOT post back. Extraction is best-effort: a field "
            + "the parser could not read confidently is left empty and flagged (confidence "
            + "`NotExtracted`), never guessed; levels, experience type and end dates are never invented; "
            + "a date arrives at the precision the document stated it in, so \"June 2015\" comes back as "
            + "`2015-06` rather than as a blank or as an invented first of the month; "
            + "and a two-column layout is warned about rather than silently reordered. Nothing is stored — "
            + "correct the draft, then submit it to POST /v1/resumes/import, the only endpoint that creates "
            + "a resume. "
            + "The draft carries an `importEvidence` token: a signed, opaque record of what the uploaded "
            + "document looked like to a parser — its column layout, whether it had a text layer, its "
            + "page count — bound to your account and valid for two hours. Post it back unchanged with "
            + "the draft and the readability engine can grade the document's ATS-parseability; drop it "
            + "and that section is renormalized out of the report instead. It is signed because it feeds "
            + "a score, so a client-asserted copy would be a score the client could set. Nothing about "
            + "the document's CONTENT is in it, and the file itself is never stored — which is also why "
            + "the evidence describes the upload rather than the resume as it later stands.");

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
            return result.ToHttpResult(page => Results.Ok(new PagedResponse<ResumeSummaryResponse>(
                [.. page.Items.Select(ResumeSummaryResponse.From)], page.NextCursor)));
        })
        .Produces<PagedResponse<ResumeSummaryResponse>>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary("Lists the caller's CVs, newest first, keyset paginated.")
        .WithDescription(
            "A SUMMARY PER ROW: contact basics, timestamps and the SIZE of each section — never the "
            + "entries themselves, and therefore no entry ids. Fetch `GET /v1/resumes/{id}` for the CV a "
            + "candidate is about to edit; that is the only route that hands out the ids `DELETE "
            + "/v1/resumes/{id}/{section}/{itemId}` takes. `nextCursor` is null on the last page and is "
            + "the only supported way to ask for more.");

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            IQueryHandler<GetResumeQuery, Result<ResumeWithItemIds>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new GetResumeQuery(httpContext.User.GetAccountId(), new ResumeId(id)), cancellationToken);
            return result.ToHttpResult(loaded => Results.Ok(ResumeResponse.From(loaded)));
        })
        .Produces<ResumeResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary("Returns one CV in full, every entry carrying the id that addresses it.")
        .WithDescription(
            "THE ONLY ROUTE THAT CARRIES ENTRY IDS. Each item of each collection has an `id` that is "
            + "stable for as long as that entry exists, and it is what `DELETE "
            + "/v1/resumes/{id}/{section}/{itemId}` takes. Ids are unique within one CV and opaque "
            + "otherwise: they are not dense, not ordered, and an entry deleted and re-added gets a new "
            + "one. Do not address an entry by its position in these arrays — the store returns each "
            + "collection as a set, so a position can name a different entry between two reads. "
            + "`GET /v1/resumes` deliberately does NOT carry the collections or their ids; fetch this "
            + "route for the CV a candidate is about to edit.");

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
            IQueryHandler<GetAnalysisHistoryQuery, Result<Page<AnalysisView>>> handler,
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
                [.. page.Items.Select(view => AnalysisResponse.From(view.Analysis, view.IsStale))], page.NextCursor)));
        })
        .Produces<PagedResponse<AnalysisResponse>>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary("Returns this resume's score history, OLDEST FIRST, keyset paginated.")
        .WithDescription(
            "The one list in this API that pages oldest first: a score history is read forwards, so "
            + "`cursor` walks toward the present. Entries are the same shape /scoring/score returns. "
            + "A section whose `breakdown.weights.<section>` is 0 was not asked about by the posting "
            + "that entry was scored against — it neither helped nor hurt, and the remaining weights are "
            + "renormalized to still total 1.0. Two entries can both report the same `schemaVersion` and "
            + "still have been scored under different weightings, because each posting asks about a "
            + "different set of sections; compare `weights` before comparing `overallScore`. Entries with "
            + "DIFFERENT `schemaVersion` values were produced by different scoring models and are not "
            + "comparable at all, whatever their weights say. "
            + "Entries are scoring EVENTS, not requests: re-scoring an unchanged resume against an "
            + "unchanged posting on the same day returns the existing run and adds nothing here. "
            + "`isStale` is computed per request against the resume as it stands now, so on any page at "
            + "most the newest entries are false — and every entry is stale once the candidate edits "
            + "again.");

        // THE HALF OF THE PRODUCT THAT NEEDS NO JOB OFFER. It hangs off the CV rather than off /scoring
        // because it is a fact about this resource and nothing else: no posting is named in the request,
        // none is read, and the call succeeds with zero job offers in the entire system.
        //
        // POST rather than GET, matching /v1/scoring/score: the request WRITES a row. A readability run
        // is a fact about a moment — the advice quotes the CV as it stood — so it is appended, never
        // recomputed over the top of an earlier one.
        //
        // Ownership is checked in the handler, exactly as it is for the analyses route above: an
        // authenticated caller who does not own this resume gets 403 and nothing is evaluated.
        group.MapPost("/{id:guid}/readability", async (
            Guid id,
            HttpContext httpContext,
            ICommandHandler<EvaluateResumeReadabilityCommand, Result<ReadabilityReport>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new EvaluateResumeReadabilityCommand(
                httpContext.User.GetAccountId(), new ResumeId(id)), cancellationToken);

            return result.ToHttpResult(report => Results.Ok(ReadabilityResponse.From(report)));
        })
        .Produces<ReadabilityResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary("Scores how readable this resume is on its own, with no job posting involved.")
        .WithDescription(
            "THE OTHER HALF OF THE SCORE, and the one that needs no job offer: this measures the CV "
            + "itself — whether the sections an ATS expects are there, whether a recruiter can reach "
            + "you, whether your bullet points state what you achieved, and whether your timeline reads "
            + "without unexplained breaks. It succeeds with no job posting in the system at all. "
            + "`readabilityScore` is NOT `overallScore` and the two must never be added together: one "
            + "grades the resume, the other grades a match against one posting. "
            + "Every `impact` is MEASURED — the exact increase in `breakdown.weightedTotal` that acting "
            + "on that one recommendation produces, computed by re-evaluating the same rule with that "
            + "one gap closed — and `priority` is a pure function of it. "
            + "A section whose `breakdown.weights.<section>` is 0 could not be measured for this resume: "
            + "it neither helped nor hurt, and the remaining weights are renormalized to still total 1.0, "
            + "so the ceiling is 100. `weights.atsParseability` is 0 unless this resume was imported with "
            + "an `importEvidence` token: that section grades the uploaded DOCUMENT — whether an ATS can "
            + "extract its text and whether it reads in one column — so a CV typed by hand has nothing "
            + "for it to measure. When it does apply, a cleanly exported single-column PDF still scores "
            + "100 overall; the section is not a tax on importing. Its advice is the only advice here "
            + "that names an edit to a FILE rather than to this resume, and acting on it means importing "
            + "the corrected document again — the file is never stored, so the signals on this resume "
            + "cannot be re-read. "
            + "Advice can be absent for a section scoring 0: a resume with no experience entries gets no "
            + "Achievements advice, because there is no role to add a bullet point to. It appears once "
            + "the work history does.");

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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

        group.MapPost("/{id:guid}/skills", async Task<IResult> (
            Guid id,
            AddSkillRequest request,
            HttpContext httpContext,
            ICommandHandler<AddSkillCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            // IsDefined for the reason spelled out in full on the languages endpoint below: TryParse
            // accepts ANY numeric string, and skill.Level is mapped to tinyint with an unchecked
            // conversion (ResumeConfiguration), so "99" stored as 99, "300" truncated to 44 and "-1"
            // wrapped to 255 — durable values that are members of neither the enum nor the column's
            // intent. Nothing scores Skill.Level today, so the damage is corrupt data rather than a
            // wrong score; the day it becomes a scoring input, a stored 255 outranks Expert.
            //
            // This guard closes undefined values only. It does NOT narrow the input to the enum's own
            // names: TryParse OR-combines comma-separated members whether or not the type is [Flags]
            // — measured, "Intermediate,Advanced" parses to 1|2 = 3 and comes back as Expert, higher
            // than either name sent, with IsDefined returning true — and it accepts a leading sign
            // ("+3" is Expert). Both remain reachable and are out of scope here: they yield a real
            // member, which is what the tinyint column and every reader downstream assume.
            SkillLevel? level = null;
            if (request.Level is not null)
            {
                if (!Enum.TryParse(request.Level, ignoreCase: true, out SkillLevel parsed)
                    || !Enum.IsDefined(parsed))
                    return Results.Problem(detail: "Invalid skill level.", statusCode: StatusCodes.Status400BadRequest);
                level = parsed;
            }

            var result = await handler.Handle(new AddSkillCommand(
                httpContext.User.GetAccountId(),
                new ResumeId(id),
                request.SkillName,
                level,
                request.YearsOfExperience), cancellationToken);
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

        group.MapPost("/{id:guid}/experiences", async Task<IResult> (
            Guid id,
            AddExperienceRequest request,
            HttpContext httpContext,
            ICommandHandler<AddExperienceCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            // Same guard, same reason as the skills endpoint above: experience.Type is tinyint and the
            // conversion is unchecked, so an undefined value is stored rather than refused.
            //
            // This one has a second consequence beyond the corrupt byte. ScoringRules splits the work
            // history on `== Professional` / `!= Professional`, so an entry typed 99 was counted as
            // unmarked experience — it failed closed and only cost the candidate who sent it, but it
            // meant a resume could hold an entry that is neither of the two types the API documents.
            // The split still reads `!= Professional` for the reason stated there; what changes is
            // that no new row can reach it that way.
            if (!Enum.TryParse(request.Type, ignoreCase: true, out ExperienceType type)
                || !Enum.IsDefined(type))
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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

        group.MapDelete("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            ICommandHandler<DeleteResumeCommand, Result<ResumeId>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new DeleteResumeCommand(httpContext.User.GetAccountId(), new ResumeId(id)), cancellationToken);
            return result.ToHttpResult(_ => Results.NoContent());
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesResultProblems()
        .ProducesAuthProblems();

        // A route of its own rather than a field on the contact update: a name is what the candidate
        // calls this CV among their others, not information about the person, and folding it in would
        // make renaming require resending an email address.
        group.MapPut("/{id:guid}/name", async (
            Guid id,
            RenameResumeRequest request,
            HttpContext httpContext,
            ICommandHandler<RenameResumeCommand, Result<Resume>> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new RenameResumeCommand(httpContext.User.GetAccountId(), new ResumeId(id), request.Name),
                cancellationToken);

            return result.ToHttpResult(resume => Results.Ok(ResumeSummaryResponse.From(resume)));
        })
        .Produces<ResumeSummaryResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems()
        .WithSummary("Names a CV, or clears the name.")
        .WithDescription(
            "A null or blank `name` CLEARS it rather than storing an empty one — a candidate cannot "
            + "tell \"not named\" from \"named nothing\", so the two are one state. Surrounding "
            + "whitespace is trimmed. Names are capped at 120 characters, which is product policy "
            + "rather than a column width: the column is encrypted and cannot overflow.");

        MapItemDeletes(group);

        return group;
    }

    /// <summary>
    /// The URL segment each collection is addressed by, paired with the section it names.
    /// </summary>
    /// <remarks>
    /// The segments are the property names <c>ResumeResponse</c> uses and the ones the existing POST
    /// routes already carry, so one CV renders from `GET /{id}`, and every entry's delete URL is its
    /// own array's name plus its own id. Stated once as a table because ten hand-written route strings
    /// is where the typo lives, and a typo here fails OPEN — the route simply never exists.
    /// </remarks>
    private static readonly (string Segment, ResumeSection Section)[] ItemSections =
    [
        ("experiences", ResumeSection.Experiences),
        ("educations", ResumeSection.Educations),
        ("skills", ResumeSection.Skills),
        ("projects", ResumeSection.Projects),
        ("certificates", ResumeSection.Certificates),
        ("languages", ResumeSection.Languages),
        ("awards", ResumeSection.Awards),
        ("publications", ResumeSection.Publications),
        ("interests", ResumeSection.Interests),
        ("references", ResumeSection.References)
    ];

    // Ten routes, one handler. Everything a per-collection copy could get wrong — the ownership check
    // above all — lives in RemoveResumeItemHandler and is written once.
    //
    // 204, not the resume. Every other write on this resource answers with the summary; a delete has
    // nothing to describe, and returning the CV would invite a client to re-render from a body whose
    // entry ids it must re-read anyway. Re-fetch GET /{id} when the ids matter.
    private static void MapItemDeletes(RouteGroupBuilder group)
    {
        foreach (var (segment, section) in ItemSections)
        {
            group.MapDelete($"/{{id:guid}}/{segment}/{{itemId:int}}", async (
                Guid id,
                int itemId,
                HttpContext httpContext,
                ICommandHandler<RemoveResumeItemCommand, Result<Resume>> handler,
                CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(
                    new RemoveResumeItemCommand(
                        httpContext.User.GetAccountId(), new ResumeId(id), section, itemId),
                    cancellationToken);

                return result.ToHttpResult(_ => Results.NoContent());
            })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesResultProblems()
            .ProducesAuthProblems()
            .WithSummary($"Removes one entry from a CV's {segment}.")
            .WithDescription(
                "`itemId` is the `id` that entry carries in `GET /v1/resumes/{id}` — the only route "
                + "that hands them out. It is NOT the entry's position in the array: these collections "
                + "come back from the store as sets, so a position can name a different entry between "
                + "two reads. An id that names no entry of this CV answers 404, including one that is "
                + "valid for a different CV. Answers 204 with no body; re-fetch the CV when you need "
                + "the ids again, because an id is not reused after its entry is removed.");
        }
    }
}

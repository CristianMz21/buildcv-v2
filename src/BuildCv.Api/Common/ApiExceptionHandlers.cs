using BuildCv.Domain.Exceptions;
using BuildCv.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace BuildCv.Api.Common;

public sealed class DomainExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case DomainException domainException:
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = "Bad Request",
                    Detail = domainException.Message,
                    Status = StatusCodes.Status400BadRequest
                }, cancellationToken);
                return true;
            case UnauthorizedAccessException:
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = "Unauthorized",
                    Status = StatusCodes.Status401Unauthorized
                }, cancellationToken);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Turns a malformed request that minimal-API parameter binding rejected into ProblemDetails, instead
/// of the empty body it would otherwise carry.
/// </summary>
/// <remarks>
/// COVERS: a body that is not valid JSON, a bare <c>null</c> body, a body exceeding
/// <c>JsonSerializerOptions.MaxDepth</c>, and a route or query value that failed to parse. All of them
/// answered an EMPTY 400 with no content type — contradicting this repository's "every error response
/// is ProblemDetails-shaped" rule and the import endpoint's own OpenAPI description.
/// <para>
/// DOES NOT COVER — and cannot — a body refused for SIZE. Measured on a real Kestrel host: a request
/// over the endpoint's <c>IRequestSizeLimitMetadata</c> ceiling answers <b>413 with
/// <c>Content-Length: 0</c> and <c>Connection: close</c></b>, and no <c>IExceptionHandler</c> is
/// invoked at all — with <c>ThrowOnBadRequest</c> both off and on. The connection is torn down inside
/// the server before the pipeline can shape anything. That asymmetry is a property of the platform,
/// not an oversight here, and it is deliberately NOT papered over with middleware: shaping only the
/// half that happens to be reachable produces two different bodies for one class of refusal.
/// </para>
/// <para>
/// It works at all only because <c>RouteHandlerOptions.ThrowOnBadRequest</c> is turned on for every
/// environment in Program.cs. Left at its default it is <c>IsDevelopment()</c>, which is why this
/// class of request answered an empty 400 in production and a logged 500 in Development — the same
/// input, two wrong answers, neither of them ProblemDetails.
/// </para>
/// <para>
/// The exception's own message is used as the detail. These are framework strings describing the
/// SHAPE of the request ("Failed to read parameter ... from the request body as JSON"), not echoes of
/// its content, so unlike the persistence handler below there is nothing here to withhold.
/// </para>
/// </remarks>
public sealed class MalformedRequestExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
            return false;

        httpContext.Response.StatusCode = badRequest.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Title = ReasonPhrases.GetReasonPhrase(badRequest.StatusCode),
                Detail = badRequest.Message,
                Status = badRequest.StatusCode
            },
            options: null,
            contentType: "application/problem+json",
            cancellationToken);
        return true;
    }
}

// Storage conflicts. Both are 409: the request was well formed and the server understood it, but another
// write got there first, and the caller's move in either case is to reload and retry.
//
// The details are fixed strings rather than the exception's message. The exceptions themselves carry a
// SqlException inner whose text names the index that fired — on this model, an index over a blind-index
// digest — and an error body is the last place that belongs.
public sealed class PersistenceExceptionHandler(ILogger<PersistenceExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var detail = exception switch
        {
            ConcurrencyConflictException => "The record was modified by another request. Reload it and try again.",
            DuplicateKeyException => "A record with the same unique value already exists.",
            ValueTooLongException => "A value in the request is too long to be stored.",
            _ => null
        };

        if (detail is null)
            return false;

        // Handled means answered, not uninteresting. A single conflict is a client retrying; a burst of
        // them is contention on one row or a duplicate-registration attempt, and without this the whole
        // class is invisible because returning true stops the 500 handler that would have logged it.
        // Warning rather than Error: the request was rejected correctly.
        logger.LogWarning(exception, "Persistence conflict on {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        // A truncation is the caller's value being wrong, not two writers racing, so it is a 400 rather
        // than the 409 the two conflict cases share.
        var status = exception is ValueTooLongException
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status409Conflict;

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = ReasonPhrases.GetReasonPhrase(status),
            Detail = detail,
            Status = status
        }, cancellationToken);
        return true;
    }
}

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception processing {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Internal Server Error",
            Detail = environment.IsDevelopment() ? exception.Message : "An unexpected error occurred.",
            Status = StatusCodes.Status500InternalServerError
        }, cancellationToken);
        return true;
    }
}

using BuildCv.Domain.Exceptions;
using BuildCv.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

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

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Conflict",
            Detail = detail,
            Status = StatusCodes.Status409Conflict
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

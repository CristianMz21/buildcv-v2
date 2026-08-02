using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace BuildCv.Api.Security;

/// <summary>
/// Enforces a per-endpoint request body ceiling declared as
/// <see cref="IRequestSizeLimitMetadata"/> — today only <c>POST /resumes/import</c>.
/// </summary>
/// <remarks>
/// IT EXISTS BECAUSE NOTHING ELSE READS THAT METADATA. <c>RequestSizeLimitAttribute</c> is an MVC filter;
/// attaching it to a minimal-API endpoint with <c>WithMetadata</c> compiles, shows up in
/// <c>Endpoint.Metadata</c>, and is then enforced by nobody. Measured against a real Kestrel host on
/// this solution: a 4 KB body to an endpoint carrying a 1 KB limit answered <b>200</b>. Without this
/// middleware the limit is decoration, which is worse than no limit because it reads like one.
/// <para>
/// TWO HALVES, and each covers what the other cannot.
/// <c>IHttpMaxRequestBodySizeFeature</c> is the real control: Kestrel enforces it while the body is
/// READ, so it bounds a chunked request that declares no <c>Content-Length</c> at all. But it is a
/// server feature, and <c>TestServer</c> does not provide one — so on the test host that half silently
/// does nothing, which is exactly the shape of a guard that is never exercised.
/// The <c>Content-Length</c> check is the half that works everywhere and refuses before a single byte of
/// the body is read. A client that lies about its length is caught by the first half in production.
/// </para>
/// <para>
/// Placed after the rate limiter and before authentication: it needs the routed endpoint (routing runs
/// at the head of the pipeline), a 413 does not depend on who is asking, and refusing an oversized body
/// is cheaper than authenticating one. It sits inside <c>UseExceptionHandler</c>, and answers
/// ProblemDetails like every other error in this API.
/// </para>
/// </remarks>
public sealed class RequestSizeLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var limit = context.GetEndpoint()?.Metadata
            .GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;

        if (limit is null)
        {
            await next(context);
            return;
        }

        // IsReadOnly once the body has started being read; nothing reads it this early, but the feature
        // documents that setting it afterwards throws.
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is not null && !sizeFeature.IsReadOnly)
            sizeFeature.MaxRequestBodySize = limit;

        if (context.Request.ContentLength > limit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Payload Too Large",
                Detail = $"The request body exceeds the {limit} byte limit for this endpoint.",
                Status = StatusCodes.Status413PayloadTooLarge
            });
            return;
        }

        await next(context);
    }
}

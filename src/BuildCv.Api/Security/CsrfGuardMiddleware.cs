using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace BuildCv.Api.Security;

// CSRF only applies to cookie-authenticated mutations; bearer-header requests carry
// no ambient credential and are not CSRF-able, so they skip validation by design.
public sealed class CsrfGuardMiddleware(RequestDelegate next)
{
    public const string CsrfHeaderName = "X-XSRF-TOKEN";

    private static readonly HashSet<string> UnsafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "DELETE", "PATCH"
    };

    private static readonly PathString[] ExemptPaths =
    [
        "/auth/antiforgery",
        "/auth/login",
        "/auth/register",
        "/auth/refresh"
    ];

    public async Task Invoke(HttpContext context, IAntiforgery antiforgery)
    {
        var request = context.Request;
        var requiresValidation =
            UnsafeMethods.Contains(request.Method)
            && !Array.Exists(ExemptPaths, path => request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))
            && !request.Headers.ContainsKey("Authorization")
            && request.Cookies.ContainsKey(AuthCookies.AccessTokenCookie);

        if (requiresValidation)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = "Forbidden",
                    Detail = "CSRF validation failed.",
                    Status = StatusCodes.Status403Forbidden
                });
                return;
            }
        }

        await next(context);
    }
}

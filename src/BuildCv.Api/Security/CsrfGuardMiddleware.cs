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

    // Matched on the request path, so these literals carry the /v1 prefix the routes mount under —
    // stale entries here fail OPEN in one direction (a moved route silently loses its exemption and
    // starts answering 403) and never fail closed, which is why both directions are pinned:
    // VersioningTests proves /v1/auth/refresh stays exempt, and SessionTerminationTests proves
    // /v1/auth/logout stays guarded.
    private static readonly PathString[] ExemptPaths =
    [
        "/v1/auth/antiforgery",
        "/v1/auth/login",
        "/v1/auth/register",
        "/v1/auth/refresh"
    ];

    public async Task Invoke(HttpContext context, IAntiforgery antiforgery)
    {
        var request = context.Request;

        // Must use the exact emptiness test the JwtBearer OnMessageReceived handler uses in
        // Program.cs: it falls back to the cookie whenever the Authorization VALUE is blank, so
        // testing only for the presence of the header KEY would let a blank `Authorization:`
        // header disarm this guard while the request still authenticates from the cookie.
        var hasBearerCredential = !string.IsNullOrWhiteSpace(request.Headers.Authorization.ToString());

        var requiresValidation =
            UnsafeMethods.Contains(request.Method)
            && !Array.Exists(ExemptPaths, path => request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))
            && !hasBearerCredential
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

                // contentType passed to WriteAsJsonAsync, not assigned to Response.ContentType first:
                // the overload without it OVERWRITES whatever is set with "application/json", so the
                // assignment that used to be on the line above never survived and this response was
                // ProblemDetails-shaped without being ProblemDetails-typed.
                await context.Response.WriteAsJsonAsync(
                    new ProblemDetails
                    {
                        Title = "Forbidden",
                        Detail = "CSRF validation failed.",
                        Status = StatusCodes.Status403Forbidden
                    },
                    options: null,
                    contentType: "application/problem+json");
                return;
            }
        }

        await next(context);
    }
}

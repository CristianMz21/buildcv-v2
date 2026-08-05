namespace BuildCv.Api.Security;

// The seven response headers this API promises on EVERY response, written at flush time rather than on
// the way in.
//
// WHY OnStarting AND NOT AN EAGER ASSIGNMENT. This middleware is registered immediately before
// UseExceptionHandler (Program.cs), so the exception handler runs INSIDE it. ExceptionHandlerMiddleware
// clears the response before invoking the IExceptionHandlers, and headers written on the way in are
// part of what it clears — so every header set here was discarded on exactly the responses whose body
// is least predictable: an RFC 7807 ProblemDetails carrying whatever threw. Measured on a real Kestrel
// host with an eager assignment: a 500 came back with the header missing, while a 200 and a 413 kept
// it. That is issue #22, and `nosniff` and `default-src 'none'` are the two it mattered most for.
//
// Reordering the two `Use` calls would fix that one clearing middleware. A callback fixes the class:
// OnStarting runs when the response is about to be flushed, so it cannot be undone by anything that
// resets the response between here and there, whoever adds it later.
//
// Measured, on the same host, so the choice is not a trade of one lost case for another: OnStarting
// fires for Kestrel's OWN refusals too. The 413 that POST /resumes/import declares — produced inside
// the server, with no IExceptionHandler involved — carries the headers under both shapes.
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task Invoke(HttpContext context)
    {
        // The state overload, and a static callback, so registering this on every request allocates no
        // closure. Registered before next() rather than after: OnStarting throws once the response has
        // started, and by the time next() returns something downstream may have flushed.
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers.Remove("Server");
            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}

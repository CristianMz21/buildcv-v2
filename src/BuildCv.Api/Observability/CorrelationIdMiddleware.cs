namespace BuildCv.Api.Observability;

/// <summary>
/// Gives every request one id, echoes it to the caller, and puts it on every log line written while
/// that request is in flight.
/// </summary>
/// <remarks>
/// <para>
/// POSITION. Registered immediately after the forwarded-headers middleware and BEFORE
/// <c>UseExceptionHandler</c>, because the log lines most worth correlating are the ones the
/// <c>IExceptionHandler</c>s write — a 500 whose id the caller was also handed is the difference
/// between "a user reports an error" and "here is the request". Nothing above this point logs.
/// </para>
/// <para>
/// THE ECHO GOES THROUGH <c>OnStarting</c>, for the reason measured in issue #22 and written up on
/// <c>SecurityHeadersMiddleware</c>: <c>ExceptionHandlerMiddleware</c> CLEARS the response before
/// invoking the handlers, so a header assigned on the way in is discarded on exactly the responses a
/// caller most needs an id for. <c>CorrelationIdTests</c> pins the exception-handled case.
/// </para>
/// <para>
/// THE INBOUND VALUE IS NOT TRUSTED. It lands in log output, so an unbounded client-controlled string
/// is a log-injection vector — a value carrying quotes, braces or separators can forge a second entry
/// inside a structured line, and a value carrying kilobytes can bury a real one. A value that does not
/// pass <see cref="IsSafeToLog"/> is REPLACED rather than trimmed or stripped: a truncated id would
/// alias two different clients' ids onto one string, and a stripped one would report an id the caller
/// never sent while looking like the one they did. Replacing loses the client's end of the correlation
/// — which it had already lost by sending something unusable — and keeps this end intact.
/// </para>
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Read from the request when a proxy already minted one, and always written back.</summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>
    /// The most a client-supplied id may be. 64 characters is longer than every id format anything is
    /// likely to send — a GUID with hyphens is 36, a W3C trace id is 32 — and short enough that a log
    /// line cannot be buried under one.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The log-scope key, and the property name a structured sink will index on.
    /// </summary>
    public const string ScopeKey = "CorrelationId";

    public async Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // StringValues.ToString() joins a repeated header with commas, and a comma is not in the safe
        // set — so a request that sends the header twice is treated as sending an unusable one and gets
        // a generated id, rather than one of the two silently winning.
        var inbound = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsSafeToLog(inbound) ? inbound : Generate();

        context.Response.OnStarting(static state =>
        {
            var (httpContext, id) = ((HttpContext, string))state;
            httpContext.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        }, (context, correlationId));

        // Begun on this middleware's own logger, which is enough for all of them: ILoggerFactory hands
        // every logger it creates the same IExternalScopeProvider, so a scope opened here is visible to
        // every ILogger resolved anywhere downstream in the request.
        //
        // The message-format overload rather than a dictionary, because it produces a state that is
        // both a rendered string and an IReadOnlyList<KeyValuePair<string, object>> carrying
        // CorrelationId as a named property — so a plain console sink and a structured one both see it.
        using (logger.BeginScope("{" + ScopeKey + "}", correlationId))
            await next(context);
    }

    /// <summary>
    /// ASCII letters, digits and hyphen, one to <see cref="MaxLength"/> of them. An allowlist rather
    /// than a denylist of the characters that hurt: the set of separators, quotes and control
    /// characters that mean something to some log format is not one this repository can enumerate.
    /// </summary>
    private static bool IsSafeToLog(string value)
    {
        if (value.Length is 0 or > MaxLength)
            return false;

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                return false;
        }

        return true;
    }

    // 32 hex characters, which is inside the safe set by construction — the generated value can never
    // be the thing this middleware exists to refuse.
    private static string Generate() => Guid.NewGuid().ToString("N");
}

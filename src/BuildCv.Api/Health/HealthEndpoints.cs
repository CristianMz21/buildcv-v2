using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace BuildCv.Api.Health;

/// <summary>
/// The two probes an orchestrator reads, and the split between them is the whole point.
/// </summary>
/// <remarks>
/// <para>
/// A failed LIVENESS probe restarts the process; a failed READINESS probe only stops traffic being
/// routed to it. So liveness must touch nothing outside the process: a liveness check that opened a
/// database connection would restart every instance the moment the database hiccuped — a rolling
/// restart of the whole fleet, caused by the one dependency that a restart cannot fix, at exactly the
/// moment the database can least afford a stampede of reconnections.
/// </para>
/// <para>
/// Deliberately OUTSIDE the <c>/v1</c> group. These are not part of the product's HTTP contract and
/// have no reason to move when it versions — an orchestrator's probe URL is configured in a deployment
/// manifest, not by a client library.
/// </para>
/// </remarks>
public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Predicate false: the endpoint runs NO registered check and reports Healthy on the strength of
        // having been reached at all. That is not a weaker version of readiness, it is the different
        // question — "is this process still serving requests" — and the only honest way to answer it is
        // to answer it from inside the process with nothing else in the path.
        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false })
            .WithHealthProbeConventions();

        // Tag-filtered rather than "every registered check", so adding a check does not silently join
        // readiness. See DatabaseHealthCheck.ReadinessTag.
        app.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(DatabaseHealthCheck.ReadinessTag)
        })
            .WithHealthProbeConventions();

        return app;
    }

    // The three conventions both probes need, in one place so they cannot drift apart — a readiness
    // endpoint that stayed authenticated while liveness went anonymous is a difference nobody would
    // notice until an orchestrator reported the app down.
    private static void WithHealthProbeConventions(this IEndpointConventionBuilder builder) =>
        builder
            // A probe runs before anything has a credential to present, and the response body is one
            // word: Healthy, Degraded or Unhealthy. Nothing here is worth authenticating for, and
            // requiring it would mean the fallback policy answers 401 to every probe.
            .AllowAnonymous()

            // Including the GLOBAL 100/min limiter, which is the one that would actually fire: probes
            // are the most frequent request a deployment makes and they all arrive from the same
            // handful of addresses. A 429 is not a health status, so a throttled probe reads as a
            // failed one — which restarts the instance on the liveness path and takes it out of
            // rotation on the readiness path. Both are backwards, and both happen exactly when the
            // limiter is closest to its ceiling: under load.
            .DisableRateLimiting()

            // GET only. MapHealthChecks maps EVERY method by default, which would put an unsafe-method
            // route on an anonymous, rate-limit-exempt path; CsrfGuardMiddleware would then evaluate a
            // POST /health/ready from a cookie-authenticated browser and answer 403 for a route that
            // changes nothing. Constraining the method removes the question instead of exempting it.
            .WithMetadata(new HttpMethodMetadata(["GET"]));
}

using BuildCv.Application.Common.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildCv.Api.Health;

/// <summary>
/// The one dependency check behind <c>/health/ready</c>: can this instance reach its store?
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than <c>AddDbContextCheck</c>, which lives in a package
/// (<c>Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore</c>) this repo does not carry,
/// and which would bind readiness to EF Core specifically — the store is chosen at composition and one
/// of the two options is a dictionary. <see cref="IPersistenceProbe"/> is the seam.
/// </para>
/// <para>
/// Both descriptions are fixed strings. <c>HealthCheckService</c> writes the description into its own
/// log line for every check it runs, so a description built from the store's own words would put a
/// connection failure's message — which names the host, the database and sometimes the login — into
/// the log on every failed probe. The default plaintext response writer emits only the status, so the
/// descriptions do not reach the wire today; a custom writer would change that, which is the second
/// reason they are fixed.
/// </para>
/// </remarks>
public sealed class DatabaseHealthCheck(IPersistenceProbe probe) : IHealthCheck
{
    public const string Name = "persistence";

    /// <summary>
    /// The tag <c>/health/ready</c> filters on. Liveness selects no checks at all, so a check added
    /// without this tag runs on neither endpoint — deliberate: a new dependency has to state that it
    /// belongs to readiness.
    /// </summary>
    public const string ReadinessTag = "ready";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await probe.CanReachAsync(cancellationToken)
            ? HealthCheckResult.Healthy("The store answered.")
            // context.Registration.FailureStatus, not a hard-coded Unhealthy: it is what the
            // registration declared this check's failure to mean, and hard-coding it here would make
            // the registration's argument a lie the day someone downgrades it to Degraded.
            : new HealthCheckResult(context.Registration.FailureStatus, "The store did not answer.");
    }
}

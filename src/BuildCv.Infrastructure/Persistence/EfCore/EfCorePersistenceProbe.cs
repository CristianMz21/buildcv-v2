using BuildCv.Application.Common.Services;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

/// <summary>
/// The SQL Server half of <see cref="IPersistenceProbe"/>: opens a connection and closes it again.
/// </summary>
/// <remarks>
/// <para>
/// <c>CanConnectAsync</c> rather than a query, because a query would make readiness depend on the
/// schema being migrated as well as on the server being up, and those are different failures wanting
/// different responses — one is "wait", the other is "deploy something".
/// </para>
/// <para>
/// The catch is a backstop, not the mechanism. <c>CanConnectAsync</c> answers false rather than
/// throwing for an unreachable server — measured against SQL Server, both for a refused connection and
/// for a host that does not resolve (<c>EfCorePersistenceProbeTests</c>). What it does not promise is
/// that EVERY failure arrives that way, and a readiness endpoint that 500s instead of reporting
/// not-ready is a probe an orchestrator cannot read. Nothing about the exception travels on: a
/// connection failure's message quotes the server, the database name and sometimes the login, and this
/// value reaches both a log and an HTTP response.
/// </para>
/// </remarks>
public sealed class EfCorePersistenceProbe(BuildCvDbContext dbContext) : IPersistenceProbe
{
    public async Task<bool> CanReachAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

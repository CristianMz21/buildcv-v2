using BuildCv.Application.Common.Services;

namespace BuildCv.Infrastructure.Persistence;

/// <summary>
/// The in-memory half of <see cref="IPersistenceProbe"/>. The store is a dictionary in this process, so
/// it is reachable exactly when the process is — which is what liveness already answers.
/// </summary>
/// <remarks>
/// This exists so the readiness endpoint has one shape for both providers rather than a null check that
/// silently reports ready when nothing was registered. It is NOT a stand-in that makes readiness
/// meaningless everywhere: the in-memory store is refused outside Development unless a host explicitly
/// acknowledges it, and refused in Production outright (see <c>DependencyInjection</c>), so a deployed
/// host always gets the EF probe.
/// </remarks>
public sealed class InMemoryPersistenceProbe : IPersistenceProbe
{
    public Task<bool> CanReachAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}

namespace BuildCv.Application.Common.Services;

/// <summary>
/// Answers one question and nothing else: can this process reach the store it was configured with?
/// </summary>
/// <remarks>
/// <para>
/// A port rather than a direct <c>DbContext</c> call from the Api, because the store is chosen in
/// <c>AddInfrastructure</c> and there are two of them. Registered per provider, so the readiness probe
/// asks the store that is actually wired up instead of assuming SQL Server — which is also what lets a
/// test swap in an unreachable one and observe the readiness endpoint fail while liveness does not.
/// </para>
/// <para>
/// It returns a bool rather than throwing: an unreachable database is the answer this probe exists to
/// give, not an error to surface. Implementations must swallow their own connection failures and must
/// never put the store's message — connection string, host name, credentials — into anything the caller
/// can log or return.
/// </para>
/// </remarks>
public interface IPersistenceProbe
{
    /// <summary>
    /// True when the configured store answered. Must not throw for a store that is merely down.
    /// </summary>
    Task<bool> CanReachAsync(CancellationToken cancellationToken = default);
}

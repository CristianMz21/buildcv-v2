using System.Threading.RateLimiting;
using BuildCv.Domain.Identity;

namespace BuildCv.Api.Security;

/// <summary>
/// Throttles <c>POST /auth/change-password</c> per account instead of per source address.
/// </summary>
/// <remarks>
/// <para>
/// Sharing the per-IP auth window was wrong on both sides. An office, campus, or mobile-carrier NAT
/// puts thousands of users on one address, so any single noisy client there could deny password
/// rotation to everyone behind it — denying the recovery action to exactly the people who may need
/// it. And in the other direction the IP window bought nothing, because only a caller already
/// holding a valid access token for the account can reach this endpoint, and such a caller can
/// rotate source addresses freely.
/// </para>
/// <para>
/// This limiter is acquired inside the endpoint rather than declared as a named rate-limiting
/// policy because <c>UseRateLimiter</c> runs before <c>UseAuthentication</c> — the order Microsoft
/// documents for a proxied app — so no principal exists yet when a policy partitioner is invoked.
/// Reordering the pipeline to suit one endpoint would move the global limiter behind JWT
/// validation, which is a worse trade.
/// </para>
/// <para>
/// The account lockout in <c>ChangePasswordHandler</c> remains the primary control against
/// current-password guessing; this is the request-rate ceiling on top of it.
/// </para>
/// </remarks>
public sealed class PasswordChangeRateLimiter : IDisposable
{
    public const int PermitLimit = 5;

    private readonly PartitionedRateLimiter<AccountId> _limiter =
        PartitionedRateLimiter.Create<AccountId, Guid>(account =>
            RateLimitPartition.GetFixedWindowLimiter(account.Value, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = PermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    public ValueTask<RateLimitLease> AcquireAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
        _limiter.AcquireAsync(accountId, permitCount: 1, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}

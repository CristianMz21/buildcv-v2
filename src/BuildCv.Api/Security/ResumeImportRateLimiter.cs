using System.Threading.RateLimiting;
using BuildCv.Domain.Identity;

namespace BuildCv.Api.Security;

/// <summary>
/// Throttles <c>POST /resumes/import</c> per account instead of per source address.
/// </summary>
/// <remarks>
/// <para>
/// The global 100/min per-IP limiter was the only ceiling on this route — measured, 95 imports were
/// accepted in a burst before the first 429. That is the wrong shape of ceiling for this endpoint,
/// because an accepted import is the most DURABLE write in the API: one request may create a resume
/// carrying roughly nine thousand owned rows, there is no per-account cap on how many resumes an
/// account may hold, and every one of those rows loads eagerly on every later read of that resume
/// (see the note on ResumeDraftLimits). A rejected draft costs nothing — validation is all-or-nothing
/// — so the cost being bounded here is specifically that of SUCCESS.
/// </para>
/// <para>
/// Per ACCOUNT rather than per IP for the reason CLAUDE.md gives about the /64 truncation: a corporate
/// LAN or a carrier NAT shares one per-IP window, so an IP ceiling low enough to matter here would
/// deny importing to everyone behind that address, while buying nothing against an attacker who
/// already holds a token and can rotate addresses.
/// </para>
/// <para>
/// Five per minute. A review screen submits once and resubmits after correcting the fields the last
/// answer named; three or four attempts is a bad session, and five a minute is already well past what
/// a person correcting a form produces. It bounds one account to ~10 MiB of body and ~45,000 owned
/// rows per minute.
/// </para>
/// <para>
/// Acquired inside the endpoint rather than declared as a named policy, exactly as
/// <see cref="PasswordChangeRateLimiter"/> is and for the same reason: <c>UseRateLimiter</c> runs
/// before <c>UseAuthentication</c>, so a policy partitioner has no principal to key on.
/// </para>
/// </remarks>
public sealed class ResumeImportRateLimiter : IDisposable
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

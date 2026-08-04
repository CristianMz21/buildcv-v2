using System.Threading.RateLimiting;
using BuildCv.Domain.Identity;

namespace BuildCv.Api.Security;

/// <summary>
/// Throttles <c>POST /resumes/import/extract</c> per account instead of per source address.
/// </summary>
/// <remarks>
/// <para>
/// Rate limiting is the PRIMARY defence on this route, by deliberate ruling: document parsing runs
/// synchronously inside the request, which makes extraction the most CPU-expensive request in the
/// product by a wide margin. Under the global per-IP limiter alone, one client could hold many
/// multi-megabyte parses in flight at once and press on the request thread pool itself — no parser
/// bug required. The 5 MiB body ceiling bounds what one request costs; this limiter bounds how often
/// one account may pay it.
/// </para>
/// <para>
/// Per ACCOUNT rather than per IP, exactly as <see cref="ResumeImportRateLimiter"/> argues: the /64
/// truncation means an IP window tight enough to matter would starve a whole corporate LAN or carrier
/// NAT, while an attacker rotating addresses walks around it — and this caller is ALWAYS
/// authenticated, so the account is both available and the honest unit of spend.
/// </para>
/// <para>
/// Ten per minute — double the import limiter's five, and the difference is what the two windows
/// bound. An accepted import buys durable rows that are loaded on every later read, so its budget is
/// sized to a person correcting one form. Extraction buys CPU for the duration of the request and
/// leaves nothing behind, and a person legitimately tries a few files back to back — the PDF, the
/// DOCX it came from, the export that finally has a text layer. Ten a minute is past any human upload
/// flow, and it caps one account's parse input at ~50 MiB a minute.
/// </para>
/// <para>
/// Acquired inside the endpoint rather than declared as a named policy, exactly as
/// <see cref="PasswordChangeRateLimiter"/> is and for the same reason: <c>UseRateLimiter</c> runs
/// before <c>UseAuthentication</c>, so a policy partitioner has no principal to key on.
/// </para>
/// </remarks>
public sealed class DocumentExtractionRateLimiter : IDisposable
{
    public const int PermitLimit = 10;

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

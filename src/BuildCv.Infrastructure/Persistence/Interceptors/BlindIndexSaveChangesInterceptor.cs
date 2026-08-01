using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildCv.Infrastructure.Persistence.Interceptors;

// Keeps every blind-index column in step with the encrypted column it makes searchable.
//
// It lives in an interceptor rather than in the repositories because the two columns must never be
// able to disagree. A row whose EmailHash does not match its Email is invisible to login and invisible
// to the duplicate check, while still occupying the unique index — an account that cannot be logged
// into and cannot be re-registered. Computing the digest at the single point where every write passes
// removes the possibility of one code path forgetting.
//
// Digests are written under the ACTIVE key only. Reads must use ComputeCandidates so rows written
// under a retired key still match during a rotation window.
public sealed class BlindIndexSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly AccountEmailIndex _accountEmail;
    private readonly RefreshTokenIndex _refreshToken;

    public BlindIndexSaveChangesInterceptor(AccountEmailIndex accountEmail, RefreshTokenIndex refreshToken)
    {
        ArgumentNullException.ThrowIfNull(accountEmail);
        ArgumentNullException.ThrowIfNull(refreshToken);
        _accountEmail = accountEmail;
        _refreshToken = refreshToken;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // Recomputed on Modified as well as Added, unconditionally. Both source properties are immutable
    // on their entities today, so this is almost always a no-op — but "almost always" is the wrong
    // guarantee for the column that decides whether an account can be found, and an HMAC over a short
    // string is far cheaper than the round trip it rides along with.
    private void Stamp(DbContext? context)
    {
        if (context is null)
            return;

        foreach (var entry in context.ChangeTracker.Entries<Account>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Property<byte[]>(ShadowColumns.EmailHash).CurrentValue = _accountEmail.Compute(entry.Entity.Email);
        }

        foreach (var entry in context.ChangeTracker.Entries<RefreshToken>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Property<byte[]>(ShadowColumns.TokenHash).CurrentValue = _refreshToken.Compute(entry.Entity);
        }
    }
}

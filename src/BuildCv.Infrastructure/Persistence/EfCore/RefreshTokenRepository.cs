using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Refresh tokens, against SQL Server.
//
// Like Account.Email the token column is encrypted and therefore unsearchable; unlike Account.Email the
// value arrives raw from a cookie, so the lookup takes the presented string and hashes it here.
internal sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly BuildCvDbContext _context;
    private readonly RefreshTokenIndex _tokenIndex;

    public RefreshTokenRepository(BuildCvDbContext context, RefreshTokenIndex tokenIndex)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tokenIndex);
        _context = context;
        _tokenIndex = tokenIndex;
    }

    // AsTracking: the refresh flow reads a token and then revokes it in the same unit of work, and
    // resolving that second call against an already-tracked instance is what keeps the two from being
    // two different rows.
    public async Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        return await FindAsync(token, cancellationToken);
    }

    public async Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        _context.RefreshTokens.Add(refreshToken);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    // Revocation IS the soft delete, and that is not a stylistic choice: RefreshToken carries no
    // revoked-at property, so the only thing that can make a token stop working is the DeletedAt
    // tombstone the global query filter reads. Remove() routes through AuditSaveChangesInterceptor,
    // which converts the delete into that tombstone and stamps who did it.
    //
    // A physical DELETE would also stop the token working, and would also lose the record that it was
    // ever issued. Going through the interceptor keeps the audit row and, because the unique index on
    // TokenHash is filtered on DeletedAt IS NULL, still frees the digest.
    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;

        var refreshToken = await FindAsync(token, cancellationToken);
        if (refreshToken is null)
            return;

        _context.RefreshTokens.Remove(refreshToken);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    // Session termination: logout and password change have to kill every token issued to an account,
    // not just the one on the current request. The refresh cookie is scoped to /auth/refresh, so
    // /auth/logout never receives it and can only identify the account — there is no token value to
    // pass to RevokeAsync.
    //
    // Deliberately NOT on IRefreshTokenRepository yet. The port declaration belongs to the session
    // termination branch, which adds it together with the handler and the in-memory adapter; declaring
    // it here too would leave the other two adapters unimplemented and make the two branches collide in
    // three files instead of none. The signature matches that declaration exactly, so whichever branch
    // merges second finds this implementation already satisfying the new interface member.
    //
    // The lookup is by AccountId rather than by digest, which is the one place this repository can ask
    // a question about the plaintext column without going through a blind index: AccountId is a real
    // foreign key, stored unencrypted, and RefreshTokenConfiguration puts a non-unique index on it.
    // The predicate below is the only filter, so that index is what the seek uses.
    //
    // Load-then-RemoveRange, NOT ExecuteDeleteAsync. ExecuteDeleteAsync does not go through
    // SaveChanges, so AuditSaveChangesInterceptor never sees the entries and never converts them into
    // DeletedAt tombstones: the rows would be physically deleted, losing the audit trail that
    // RevokeAsync above is careful to keep, and stamping nobody as the revoker. The cost is one round
    // trip and the tokens in memory, which is bounded by the sessions one account can hold.
    public async Task RevokeAllForAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountId);

        var tokens = await _context.RefreshTokens.AsTracking()
            .Where(refreshToken => refreshToken.AccountId == accountId)
            .ToListAsync(cancellationToken);

        // Not merely an optimization: SaveChanges on an empty change tracker still opens a transaction,
        // and "log out an account that has no live sessions" is a normal request, not an exception.
        if (tokens.Count == 0)
            return;

        _context.RefreshTokens.RemoveRange(tokens);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    private async Task<RefreshToken?> FindAsync(string token, CancellationToken cancellationToken) =>
        await BlindIndexLookup.FirstMatchAsync(
            _tokenIndex.ComputeCandidates(token),
            digest => _context.RefreshTokens.AsTracking()
                .Where(refreshToken => EF.Property<byte[]>(refreshToken, ShadowColumns.TokenHash) == digest),
            cancellationToken);
}

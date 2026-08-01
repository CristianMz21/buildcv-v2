using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;

namespace BuildCv.Infrastructure.Persistence;

public sealed class InMemoryRefreshTokenRepository : IRefreshTokenRepository
{
    private readonly ConcurrentDictionary<string, RefreshToken> _tokens = new(StringComparer.Ordinal);

    public Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tokens.TryGetValue(token, out var refreshToken);
        return Task.FromResult(refreshToken);
    }

    public Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tokens[refreshToken.Token] = refreshToken;
        return Task.CompletedTask;
    }

    public Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tokens.TryRemove(token, out _);
        return Task.CompletedTask;
    }

    public Task RevokeAllForAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ConcurrentDictionary supports removal while enumerating. A token issued for this account
        // after enumeration started may survive; that is the same race any store has, and it only
        // covers sessions created after the revocation request arrived.
        foreach (var entry in _tokens)
        {
            if (entry.Value.AccountId == accountId)
                _tokens.TryRemove(entry.Key, out _);
        }

        return Task.CompletedTask;
    }
}

using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;

namespace BuildCv.Infrastructure.Persistence;

public sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly ConcurrentDictionary<Guid, Account> _accounts = new();

    public Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _accounts.TryGetValue(id.Value, out var account);
        return Task.FromResult(account);
    }

    public Task<Account?> GetByEmailAsync(Email email, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = _accounts.Values.FirstOrDefault(
            a => string.Equals(a.Email.Value, email.Value, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(account);
    }

    public Task<bool> ExistsByEmailAsync(Email email, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = _accounts.Values.Any(
            a => string.Equals(a.Email.Value, email.Value, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(exists);
    }

    public Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _accounts[account.Id.Value] = account;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _accounts[account.Id.Value] = account;
        return Task.CompletedTask;
    }
}

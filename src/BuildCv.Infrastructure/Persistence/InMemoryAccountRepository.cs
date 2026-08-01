using System.Collections.Concurrent;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;

namespace BuildCv.Infrastructure.Persistence;

// The development and test store. It has to answer the ports the same way the SQL Server ones do, or
// tests written against it certify behavior that does not exist in production.
//
// Which is why Status == Deleted is filtered out of every lookup below. Under EF a domain delete writes
// the DeletedAt tombstone alongside the status, and the global query filter plus the filtered unique
// index on EmailHash then make the account invisible and free its address for re-registration. A
// dictionary has neither, so the equivalent is stated explicitly here — otherwise the first
// "delete my account" endpoint would get a full green Api suite against semantics that are the exact
// opposite of production's: account still findable, address still locked.
public sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly ConcurrentDictionary<Guid, Account> _accounts = new();

    public Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _accounts.TryGetValue(id.Value, out var account);
        return Task.FromResult(IsLive(account) ? account : null);
    }

    public Task<Account?> GetByEmailAsync(Email email, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = _accounts.Values.FirstOrDefault(a => MatchesLiveAccount(a, email));
        return Task.FromResult(account);
    }

    public Task<bool> ExistsByEmailAsync(Email email, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = _accounts.Values.Any(a => MatchesLiveAccount(a, email));
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

    // Kept, not removed, exactly like the tombstoned row: the record survives, it just stops answering.
    private static bool IsLive(Account? account) => account is { Status: not AccountStatus.Deleted };

    private static bool MatchesLiveAccount(Account account, Email email) =>
        IsLive(account) && string.Equals(account.Email.Value, email.Value, StringComparison.OrdinalIgnoreCase);
}

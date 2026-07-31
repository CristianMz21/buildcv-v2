namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;

public sealed class FakeAccountRepository : IAccountRepository
{
    private readonly List<Account> _accounts = [];

    public Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.FirstOrDefault(a => a.Id == id));

    public Task<Account?> GetByEmailAsync(Email email, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.FirstOrDefault(a => a.Email == email));

    public Task<bool> ExistsByEmailAsync(Email email, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.Any(a => a.Email == email));

    public Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        _accounts.Add(account);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
    {
        var index = _accounts.FindIndex(a => a.Id == account.Id);
        if (index >= 0)
            _accounts[index] = account;
        return Task.CompletedTask;
    }
}

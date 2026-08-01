using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Accounts, against SQL Server.
//
// Every read here is a lookup by an ENCRYPTED column, which is exactly the thing LINQ cannot do: the
// ciphertext carries a fresh nonce per write, so no two rows holding the same address share bytes.
// `Where(account => account.Email == email)` would compile, run, and return nothing, forever. The
// lookups go through the blind index instead, and BlindIndexLookup is what makes that the only shape
// that type-checks.
internal sealed class AccountRepository : IAccountRepository
{
    private readonly BuildCvDbContext _context;
    private readonly AccountEmailIndex _emailIndex;
    private readonly TimeProvider _timeProvider;

    public AccountRepository(BuildCvDbContext context, AccountEmailIndex emailIndex, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(emailIndex);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _context = context;
        _emailIndex = emailIndex;
        _timeProvider = timeProvider;
    }

    // AsTracking, against the context-wide NoTracking default: every caller of this method goes on to
    // mutate what it gets back — RecordFailedLogin, VerifyEmail, ChangePassword — and hand it to
    // UpdateAsync. An untracked entity there would have to be re-attached, and re-attaching a root whose
    // rowversion is shadow state silently discards the concurrency check.
    public async Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return await _context.Accounts.AsTracking().FirstOrDefaultAsync(account => account.Id == id, cancellationToken);
    }

    public async Task<Account?> GetByEmailAsync(Email email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        return await BlindIndexLookup.FirstMatchAsync(
            _emailIndex.ComputeCandidates(email),
            digest => _context.Accounts.AsTracking()
                .Where(account => EF.Property<byte[]>(account, ShadowColumns.EmailHash) == digest),
            cancellationToken);
    }

    // Registration's pre-check. It runs through the same candidate digests as the login lookup on
    // purpose: if the two ever disagreed about which digests count as "this address", one of them would
    // be wrong about whether the account exists, and the disagreement would land as a duplicate identity.
    public async Task<bool> ExistsByEmailAsync(Email email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        return await BlindIndexLookup.AnyMatchAsync(
            _emailIndex.ComputeCandidates(email),
            digest => _context.Accounts.Where(account => EF.Property<byte[]>(account, ShadowColumns.EmailHash) == digest),
            cancellationToken);
    }

    public async Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        _context.Accounts.Add(account);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    public async Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        // Throws when the aggregate did not come from this repository. See TrackedAggregateExtensions:
        // a detached root carries no rowversion, so writing it is not a slower path but an unverifiable
        // one.
        var entry = _context.RequireTracked(account);

        // The domain half of the delete already happened inside Account.Delete(); this is the other
        // half. Both land in the same UPDATE, so the row is never observable as "status Deleted but
        // still live" or as "tombstoned but still Active".
        if (account.Status is AccountStatus.Deleted)
            entry.MarkTombstoned(_timeProvider.GetUtcNow());

        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }
}

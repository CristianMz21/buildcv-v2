using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

// Accounts. The one table where the split between "encrypted" and "queryable" is load-bearing:
// Email is confidential, and the login path still has to find a row by it.
internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    private readonly IFieldEncryptor _encryptor;

    public AccountConfiguration(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        _encryptor = encryptor;
    }

    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Accounts", "identity");

        // Nonclustered: the clustered index belongs to Seq. A random Guid as the clustered key would
        // scatter every insert across the whole table.
        builder.HasKey(account => account.Id).IsClustered(false);
        builder.Property(account => account.Id).HasColumnName("Id").ValueGeneratedNever();

        builder.HasAuditColumns();
        builder.HasSoftDelete();
        builder.HasRowVersion();
        builder.HasKeysetSequence();

        // CONFIDENTIAL. varbinary, and deliberately NOT indexed: the ciphertext carries a fresh nonce
        // on every write, so two rows holding the same address have different bytes and an index on
        // them can neither enforce uniqueness nor answer a lookup.
        builder.Property(account => account.Email)
            .HasColumnName("Email")
            .IsRequired()
            .IsEncryptedEmail(_encryptor, AccountEmailIndex.Context);

        // ...which is what this column is for. A deterministic keyed digest of the same address: it
        // can carry the unique index the encrypted column cannot, and it is what the login path
        // matches on. Written by BlindIndexSaveChangesInterceptor, never by the Domain.
        builder.Property<byte[]>(ShadowColumns.EmailHash)
            .HasColumnType("binary(32)")
            .IsRequired()
            .HasAnnotation(PersistenceAnnotations.BlindIndexFor, AccountEmailIndex.Context);

        // Filtered on DeletedAt so a soft-deleted account frees its address for re-registration.
        // Without the filter, a tombstoned row would keep the address locked forever with no visible
        // reason.
        builder.HasIndex(ShadowColumns.EmailHash)
            .IsUnique()
            .HasFilter($"[{ShadowColumns.DeletedAt}] IS NULL");

        // Not encrypted, on purpose: it is already a one-way salted Argon2id digest. Encrypting a
        // hash buys nothing and adds a key-loss failure mode to the login path.
        builder.Property(account => account.Password)
            .HasColumnName("PasswordHash")
            .HasConversion<PasswordConverter>()
            .HasMaxLength(PasswordConverter.MaxLength)
            .IsRequired();

        // ANALYTICAL, plaintext. "Accounts by role", "how many are suspended" are the internal
        // questions this schema exists to answer, and tinyint keeps the enums narrow and indexable.
        builder.Property(account => account.Role)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(account => account.Status)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(account => account.CreatedAt).IsRequired();
        builder.Property(account => account.UpdatedAt).IsRequired();
        builder.Property(account => account.EmailVerifiedAt);
        builder.Property(account => account.LastLoginAt);

        // Security counters, not personal data: plaintext so a lockout can be reasoned about in SQL.
        builder.Property(account => account.FailedLoginCount).IsRequired();
        builder.Property(account => account.LockedUntil);

        // Derived from state already stored. Persisting them would create a second source of truth
        // that goes stale the moment a lockout expires.
        builder.Ignore(account => account.IsEmailVerified);
        builder.Ignore(account => account.IsLocked);
        builder.Ignore(account => account.CanPostJobs);
    }
}

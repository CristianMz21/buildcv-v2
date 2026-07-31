using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

// Refresh tokens get their own table rather than being owned by Account: IRefreshTokenRepository
// loads and revokes them without the account, and an owned collection would drag every token into
// memory on every account read.
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    // The Domain models a refresh token by its value, not by an identity, so there is no Id property
    // to key on. The alternative was keying on TokenHash, which is genuinely unique — rejected
    // because it makes a security-sensitive digest the target of every future foreign key, and a
    // blind-index rotation would then have to rewrite referencing rows rather than one column.
    private const string IdShadowProperty = "Id";

    private readonly IFieldEncryptor _encryptor;

    public RefreshTokenConfiguration(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        _encryptor = encryptor;
    }

    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens", "identity");

        builder.Property<Guid>(IdShadowProperty).ValueGeneratedOnAdd();
        builder.HasKey(IdShadowProperty).IsClustered(false);

        builder.HasAuditColumns();
        builder.HasSoftDelete();
        builder.HasRowVersion();
        builder.HasKeysetSequence();

        // CONFIDENTIAL. A refresh token is a bearer credential: anyone holding the plaintext is the
        // user until it expires, which makes it strictly more sensitive than the password hash next
        // to it.
        builder.Property(refreshToken => refreshToken.Token)
            .HasColumnName("Token")
            .IsRequired()
            .IsEncryptedText(_encryptor, RefreshTokenIndex.Context, EncryptedColumn.Token);

        // The lookup column. The refresh endpoint receives a token and has to find its row; it
        // cannot scan and decrypt every row to do it.
        builder.Property<byte[]>(ShadowColumns.TokenHash)
            .HasColumnType("binary(32)")
            .IsRequired()
            .HasAnnotation(PersistenceAnnotations.BlindIndexFor, RefreshTokenIndex.Context);

        builder.HasIndex(ShadowColumns.TokenHash)
            .IsUnique()
            .HasFilter($"[{ShadowColumns.DeletedAt}] IS NULL");

        // Real foreign key, no navigation: the Domain deliberately holds an AccountId rather than an
        // Account, and adding a navigation here purely to satisfy EF would put a persistence concern
        // into the aggregate.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(refreshToken => refreshToken.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(refreshToken => refreshToken.AccountId);

        builder.Property(refreshToken => refreshToken.CreatedAt).IsRequired();
        builder.Property(refreshToken => refreshToken.ExpiresAt).IsRequired();

        // Drives the expired-token sweep, which is a range scan over this column alone.
        builder.HasIndex(refreshToken => refreshToken.ExpiresAt);

        // Computed from ExpiresAt against the current clock. A persisted copy would be wrong within
        // a second of being written.
        builder.Ignore(refreshToken => refreshToken.IsExpired);
    }
}

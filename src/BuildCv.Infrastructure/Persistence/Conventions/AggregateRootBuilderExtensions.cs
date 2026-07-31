using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Conventions;

// The shape every aggregate root table shares. Each root configuration calls all four explicitly
// rather than having a base class or a convention apply them: a reader of AccountConfiguration can
// see the whole table shape without opening anything else, and a root that deliberately opts out of
// one of them shows up as a missing call instead of an invisible override.
internal static class AggregateRootBuilderExtensions
{
    // Shadow Guid columns rather than a foreign key to Accounts: audit rows must survive the account
    // they point at being purged, and a real FK would either block the purge or cascade the history
    // away with it.
    public static EntityTypeBuilder<TEntity> HasAuditColumns<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<Guid?>(ShadowColumns.CreatedBy);
        builder.Property<Guid?>(ShadowColumns.UpdatedBy);
        return builder;
    }

    // Soft delete plus the global filter that makes it mean something. Every query on this root
    // silently excludes tombstoned rows; a caller that genuinely needs them must say
    // IgnoreQueryFilters(), which is a visible, greppable decision.
    public static EntityTypeBuilder<TEntity> HasSoftDelete<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<DateTimeOffset?>(ShadowColumns.DeletedAt);
        builder.Property<Guid?>(ShadowColumns.DeletedBy);
        builder.HasQueryFilter(entity => EF.Property<DateTimeOffset?>(entity, ShadowColumns.DeletedAt) == null);
        return builder;
    }

    // SQL Server rowversion: the database bumps it on every UPDATE, and EF puts the value it read
    // into the WHERE clause. A lost update surfaces as a DbUpdateConcurrencyException instead of one
    // writer's changes silently disappearing.
    public static EntityTypeBuilder<TEntity> HasRowVersion<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<byte[]>(ShadowColumns.RowVersion).IsRowVersion();
        return builder;
    }

    // The monotonic insert order the public Guid id cannot provide.
    //
    // Two jobs. First, it is the CLUSTERED unique index, so rows are laid out in insert order and
    // inserts always land at the end of the B-tree; clustering on a random Guid instead would
    // fragment every page split across the whole table. Second, it is a stable, total ordering for
    // keyset pagination, which CreatedAt is not — two rows can share a timestamp.
    public static EntityTypeBuilder<TEntity> HasKeysetSequence<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<long>(ShadowColumns.Seq)
            .ValueGeneratedOnAdd()
            .UseIdentityColumn();

        builder.HasIndex(ShadowColumns.Seq)
            .IsUnique()
            .IsClustered();

        return builder;
    }
}

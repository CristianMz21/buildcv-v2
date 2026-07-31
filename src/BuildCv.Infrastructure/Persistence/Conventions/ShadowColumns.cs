namespace BuildCv.Infrastructure.Persistence.Conventions;

// Names of the shadow columns every aggregate root carries. They are shadow state on purpose: the
// Domain has no opinion about who wrote a row or when it was tombstoned, and giving it one would put
// infrastructure concerns inside entities that are meant to be persistence-ignorant.
internal static class ShadowColumns
{
    // bigint IDENTITY. The clustered key and the keyset-pagination cursor; the public Id stays a
    // random Guid, which is exactly what must not be clustered on.
    public const string Seq = "Seq";

    public const string CreatedBy = "CreatedBy";
    public const string UpdatedBy = "UpdatedBy";

    // Soft delete. The global query filter keys off DeletedAt, so a tombstoned row disappears from
    // every query that does not explicitly opt out.
    public const string DeletedAt = "DeletedAt";
    public const string DeletedBy = "DeletedBy";

    public const string RowVersion = "RowVersion";

    // Blind-index companions for the encrypted columns that need exact-match lookup.
    public const string EmailHash = "EmailHash";
    public const string TokenHash = "TokenHash";
}

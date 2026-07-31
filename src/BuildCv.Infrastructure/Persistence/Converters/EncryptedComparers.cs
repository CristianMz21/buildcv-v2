using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace BuildCv.Infrastructure.Persistence.Converters;

// Converted reference types need an explicit comparer. EF's default for a converted property falls
// back to comparing the PROVIDER value, and an encrypted provider value is a fresh random-nonce
// envelope on every conversion — every entity would look modified on every SaveChanges. These
// comparers work on the model value instead.
internal static class EncryptedComparers
{
    // For the immutable value objects and records the Domain exposes: value equality, no snapshot copy.
    public static ValueComparer<T> ForValueObject<T>()
        where T : class =>
        new(
            (left, right) => left == null ? right == null : left.Equals(right),
            value => value.GetHashCode(),
            value => value);

    // List-shaped members (Highlights, Keywords, Technologies, Profiles, Recommendations) are
    // replaced wholesale by the Domain, but EF still needs order-sensitive equality plus a snapshot
    // it can diff against.
    public static ValueComparer<IReadOnlyList<T>> ForList<T>()
        where T : notnull =>
        new(
            (left, right) => left == null || right == null ? left == right : left.SequenceEqual(right),
            value => value.Aggregate(0, (hash, element) => HashCode.Combine(hash, element.GetHashCode())),
            value => (IReadOnlyList<T>)value.ToArray());
}

using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace BuildCv.Infrastructure.Persistence.Converters;

// Comparers for CONVERTED reference-typed properties, encrypted or not.
//
// EF's default for a converted property compares the PROVIDER value. For an encrypted column that is
// catastrophic — the provider value is a fresh random-nonce envelope on every conversion, so every
// tracked entity looks modified on every SaveChanges — and for a JSON list column it is merely
// wasteful, since it serializes both sides to compare them. These work on the model value instead.
//
// Named for the conversion rather than the encryption on purpose: ForList is used by deliberately
// PLAINTEXT columns too (Skill.Keywords, Project.Technologies, Analysis.Recommendations). A name
// implying encryption would tell the next person auditing the data classification something false
// about those three.
internal static class ConvertedComparers
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

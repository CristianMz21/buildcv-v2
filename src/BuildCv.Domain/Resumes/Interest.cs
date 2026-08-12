using BuildCv.Domain.Common;

namespace BuildCv.Domain.Resumes;

public sealed record Interest(
    string Name)
{
    public IReadOnlyList<string> Keywords { get; init; } = [];

    // The record's synthesized equality compares Keywords BY REFERENCE, so two interests carrying the
    // same keywords were not equal unless they were the same instance. The profile's idempotent Add
    // relies on value equality across imports, where the lists are always freshly built — see
    // SequenceEquality.
    public bool Equals(Interest? other) =>
        other is not null
        && Name == other.Name
        && SequenceEquality.Equal(Keywords, other.Keywords);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(SequenceEquality.Hash(Keywords));
        return hash.ToHashCode();
    }
}

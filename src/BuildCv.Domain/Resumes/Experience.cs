using BuildCv.Domain.Common;
using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Experience(
    ExperienceType Type,
    OrganizationName Organization,
    string Position,
    DateRange Period,
    string? Summary = null)
{
    public IReadOnlyList<string> Highlights { get; init; } = [];

    // The record's synthesized equality compares Highlights BY REFERENCE, so two jobs holding the same
    // bullets were not equal unless they were the same instance. The profile's idempotent Add relies on
    // value equality across imports, where the lists are always freshly built — see SequenceEquality.
    public bool Equals(Experience? other) =>
        other is not null
        && Type == other.Type
        && Organization == other.Organization
        && Position == other.Position
        && Period == other.Period
        && Summary == other.Summary
        && SequenceEquality.Equal(Highlights, other.Highlights);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.Add(Organization);
        hash.Add(Position);
        hash.Add(Period);
        hash.Add(Summary);
        hash.Add(SequenceEquality.Hash(Highlights));
        return hash.ToHashCode();
    }
}

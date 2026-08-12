using BuildCv.Domain.Common;
using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Project(
    string Name,
    DateRange Period,
    string? Description = null,
    Url? RepositoryUrl = null,
    Url? LiveDemoUrl = null)
{
    public IReadOnlyList<Technology> Technologies { get; init; } = [];
    public IReadOnlyList<string> Highlights { get; init; } = [];

    // The record's synthesized equality compares these two lists BY REFERENCE, so two projects holding
    // the same technology stack were not equal unless it was the same instance. The profile's
    // idempotent Add relies on value equality across imports, where the lists are always freshly built
    // — see SequenceEquality.
    public bool Equals(Project? other) =>
        other is not null
        && Name == other.Name
        && Period == other.Period
        && Description == other.Description
        && RepositoryUrl == other.RepositoryUrl
        && LiveDemoUrl == other.LiveDemoUrl
        && SequenceEquality.Equal(Technologies, other.Technologies)
        && SequenceEquality.Equal(Highlights, other.Highlights);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Period);
        hash.Add(Description);
        hash.Add(RepositoryUrl);
        hash.Add(LiveDemoUrl);
        hash.Add(SequenceEquality.Hash(Technologies));
        hash.Add(SequenceEquality.Hash(Highlights));
        return hash.ToHashCode();
    }
}

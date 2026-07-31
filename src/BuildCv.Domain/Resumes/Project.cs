using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Project(
    string Name,
    DateRange Period,
    string? Description = null,
    Url? RepositoryUrl = null,
    Url? LiveDemoUrl = null)
{
    public IReadOnlyList<string> Technologies { get; init; } = [];
    public IReadOnlyList<string> Highlights { get; init; } = [];
}

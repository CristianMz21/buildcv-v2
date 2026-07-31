namespace BuildCv.Domain.Resumes;

public sealed record Project(
    string Name,
    string? Description,
    DateRange Period,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Highlights,
    string? RepositoryUrl,
    string? LiveDemoUrl);

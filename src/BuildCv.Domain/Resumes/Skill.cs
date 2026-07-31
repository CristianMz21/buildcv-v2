namespace BuildCv.Domain.Resumes;

public sealed record Skill(
    string Name,
    string? Level = null)
{
    public IReadOnlyList<string> Keywords { get; init; } = [];
}

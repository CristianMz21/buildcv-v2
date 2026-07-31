using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Skill(
    Technology Name,
    string? Level = null)
{
    public IReadOnlyList<string> Keywords { get; init; } = [];
}

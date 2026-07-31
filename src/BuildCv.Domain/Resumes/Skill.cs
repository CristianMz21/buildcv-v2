namespace BuildCv.Domain.Resumes;

public sealed record Skill(
    string Name,
    string? Level,
    IReadOnlyList<string> Keywords);

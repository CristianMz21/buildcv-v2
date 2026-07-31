namespace BuildCv.Domain.Resumes;

public sealed record Interest(
    string Name,
    IReadOnlyList<string> Keywords);

namespace BuildCv.Domain.Resumes;

public sealed record Award(
    string Title,
    string? Awarder,
    string? Date,
    string? Summary);

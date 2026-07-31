namespace BuildCv.Domain.Resumes;

public sealed record Award(
    string Title,
    string? Awarder,
    DateOnly? Date,
    string? Summary);

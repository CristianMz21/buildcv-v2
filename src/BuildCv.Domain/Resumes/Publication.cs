namespace BuildCv.Domain.Resumes;

public sealed record Publication(
    string Title,
    string? Publisher,
    string? Url,
    string? ReleaseDate,
    string? Summary);

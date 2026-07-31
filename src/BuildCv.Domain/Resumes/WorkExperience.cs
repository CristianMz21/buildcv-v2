namespace BuildCv.Domain.Resumes;

public sealed record WorkExperience(
    string Company,
    string Position,
    DateRange Period,
    string? Summary,
    IReadOnlyList<string> Highlights);

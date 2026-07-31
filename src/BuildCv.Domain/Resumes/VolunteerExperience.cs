namespace BuildCv.Domain.Resumes;

public sealed record VolunteerExperience(
    string Organization,
    string Position,
    DateRange Period,
    string? Summary,
    IReadOnlyList<string> Highlights);

using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record VolunteerExperience(
    string Organization,
    string Position,
    DateRange Period,
    string? Summary = null)
{
    public IReadOnlyList<string> Highlights { get; init; } = [];
}

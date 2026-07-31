namespace BuildCv.Domain.Jobs;

public sealed record JobPosting(
    string Title,
    string Company,
    string? Description = null)
{
    public IReadOnlyList<JobRequirement> Requirements { get; init; } = [];
}

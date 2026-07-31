namespace BuildCv.Domain.Jobs;

public sealed record JobPosting(
    string Title,
    string Company,
    IReadOnlyList<JobRequirement> Requirements,
    string? Description);

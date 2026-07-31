using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Jobs;

public sealed record JobPosting(
    string Title,
    OrganizationName Company,
    string? Description = null)
{
    public IReadOnlyList<JobRequirement> Requirements { get; init; } = [];
}

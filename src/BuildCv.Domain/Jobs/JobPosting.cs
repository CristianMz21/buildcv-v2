using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

namespace BuildCv.Domain.Jobs;

public sealed record JobPosting(
    AccountId OwnerId,
    string Title,
    OrganizationName Company,
    string? Description = null,
    OrganizationId? OrgId = null)
{
    public IReadOnlyList<JobRequirement> Requirements { get; init; } = [];
}

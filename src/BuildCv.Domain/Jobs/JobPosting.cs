namespace BuildCv.Domain.Jobs;

public class JobPosting
{
    public required string Title { get; init; }
    public required string Company { get; init; }
    public List<JobRequirement> Requirements { get; init; } = [];
    public string? Description { get; init; }
}

public class JobRequirement
{
    public required string Skill { get; init; }
    public required RequirementPriority Priority { get; init; }
    public double Weight { get; init; } = 1.0;
}

public enum RequirementPriority
{
    MustHave,
    NiceToHave,
    Responsibility
}

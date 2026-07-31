namespace BuildCv.Domain.Jobs;

public sealed record JobRequirement(
    string Skill,
    RequirementPriority Priority,
    double Weight = 1.0);

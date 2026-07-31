using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Jobs;

public sealed record JobRequirement
{
    public Technology Skill { get; }
    public RequirementPriority Priority { get; }
    public double Weight { get; }

    private JobRequirement(Technology skill, RequirementPriority priority, double weight)
    {
        Skill = skill;
        Priority = priority;
        Weight = weight;
    }

    public static JobRequirement Create(Technology skill, RequirementPriority priority, double weight = 1.0)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (weight is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weight must be between 0 and 10.");
        return new JobRequirement(skill, priority, weight);
    }
}

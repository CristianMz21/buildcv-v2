namespace BuildCv.Domain.Jobs;

public sealed record JobRequirement(
    string Skill,
    RequirementPriority Priority,
    double Weight = 1.0)
{
    public double Weight { get; init; } = ValidateWeight(Weight);

    private static double ValidateWeight(double weight) =>
        weight is < 0 or > 10
            ? throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weight must be between 0 and 10.")
            : weight;
}

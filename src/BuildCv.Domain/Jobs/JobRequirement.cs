using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Jobs;

// Two signals of importance used to live here with nothing tying them together: Priority said
// "must have", Weight said "how much", and a caller could write MustHave with a weight of 0.1
// without anything objecting. Weight is now the MAGNITUDE and Priority the GATE, and an unspecified
// weight is derived FROM the priority so the two can never contradict each other.
public sealed record JobRequirement
{
    // The two derived magnitudes. They are the numbers the scoring engine has always computed inline
    // from Priority, kept identical here on purpose so moving the rule into the factory moves no
    // score. PR 3 makes the engine read Weight instead and use Priority only to decide whether an
    // unmet requirement is worth a Critical recommendation.
    private const double MustHaveWeight = 1.0;
    private const double NiceToHaveWeight = 0.5;

    public Technology Skill { get; }
    public RequirementPriority Priority { get; }
    public double Weight { get; }

    private JobRequirement(Technology skill, RequirementPriority priority, double weight)
    {
        Skill = skill;
        Priority = priority;
        Weight = weight;
    }

    public static JobRequirement Create(Technology skill, RequirementPriority priority, double? weight = null)
    {
        ArgumentNullException.ThrowIfNull(skill);
        // `null` matches neither arm, so an omitted weight falls through to the default below rather
        // than being range-checked against a value the caller never supplied.
        if (weight is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weight must be between 0 and 10.");
        return new JobRequirement(skill, priority, weight ?? DefaultWeightFor(priority));
    }

    private static double DefaultWeightFor(RequirementPriority priority) =>
        priority == RequirementPriority.MustHave ? MustHaveWeight : NiceToHaveWeight;
}

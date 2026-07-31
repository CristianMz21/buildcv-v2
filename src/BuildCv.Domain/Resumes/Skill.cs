using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Skill
{
    private const int MaxYearsOfExperience = 60;

    public Technology Name { get; }
    public SkillLevel? Level { get; }
    public int? YearsOfExperience { get; }

    private Skill(Technology name, SkillLevel? level, int? yearsOfExperience)
    {
        Name = name;
        Level = level;
        YearsOfExperience = yearsOfExperience;
    }

    public static Skill Create(Technology name, SkillLevel? level = null, int? yearsOfExperience = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (yearsOfExperience.HasValue && (yearsOfExperience.Value < 0 || yearsOfExperience.Value > MaxYearsOfExperience))
            throw new ArgumentOutOfRangeException(nameof(yearsOfExperience), yearsOfExperience, $"YearsOfExperience must be between 0 and {MaxYearsOfExperience}.");

        return new Skill(name, level, yearsOfExperience);
    }

    public IReadOnlyList<string> Keywords { get; init; } = [];
}

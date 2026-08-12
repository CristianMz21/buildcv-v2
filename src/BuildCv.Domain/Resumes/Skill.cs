using BuildCv.Domain.Common;
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

    // The record's synthesized equality compares Keywords BY REFERENCE, so two skills carrying the same
    // keywords were not equal unless they were the same instance. The profile's idempotent Add relies
    // on value equality across imports, where the lists are always freshly built — see SequenceEquality.
    public bool Equals(Skill? other) =>
        other is not null
        && Name == other.Name
        && Level == other.Level
        && YearsOfExperience == other.YearsOfExperience
        && SequenceEquality.Equal(Keywords, other.Keywords);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Level);
        hash.Add(YearsOfExperience);
        hash.Add(SequenceEquality.Hash(Keywords));
        return hash.ToHashCode();
    }
}

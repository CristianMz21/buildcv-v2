namespace BuildCv.Domain.Scoring;

public sealed record ScoringWeightsSnapshot
{
    // The version the six-weight model is stored under. Bumped from 1 when Languages was added, so an
    // analysis scored before the change still says which model explained it.
    public const int CurrentSchemaVersion = 2;

    public double Skills { get; }
    public double Experience { get; }
    public double Education { get; }
    public double Certifications { get; }
    public double Projects { get; }
    public double Languages { get; }
    public int SchemaVersion { get; }

    private ScoringWeightsSnapshot(
        double skills,
        double experience,
        double education,
        double certifications,
        double projects,
        double languages,
        int schemaVersion)
    {
        Skills = skills;
        Experience = experience;
        Education = education;
        Certifications = certifications;
        Projects = projects;
        Languages = languages;
        SchemaVersion = schemaVersion;
    }

    public static ScoringWeightsSnapshot Create(
        double skills,
        double experience,
        double education,
        double certifications,
        double projects,
        double languages,
        int schemaVersion = CurrentSchemaVersion)
    {
        if (skills < 0 || experience < 0 || education < 0 || certifications < 0 || projects < 0 || languages < 0)
            throw new ArgumentException("Weights must be non-negative.");

        // The invariant everything downstream leans on. It is what makes WeightedTotal a 0..1 number,
        // which is what makes Analysis.OverallScore a percentage, which is what makes ScoreBand's
        // thresholds mean anything. A five-member v1 payload still satisfies it: Languages reads back
        // as 0.0 and the other five already summed to 1.0.
        var sum = skills + experience + education + certifications + projects + languages;
        if (Math.Abs(sum - 1.0) > 0.0001)
            throw new ArgumentException($"Weights must sum to 1.0 (actual: {sum}).");

        if (schemaVersion < 1)
            throw new ArgumentException("SchemaVersion must be >= 1.", nameof(schemaVersion));

        return new ScoringWeightsSnapshot(
            skills, experience, education, certifications, projects, languages, schemaVersion);
    }

    public static ScoringWeightsSnapshot Default() => Create(0.45, 0.20, 0.10, 0.10, 0.05, 0.10);

    public double WeightFor(SectionType section) => section switch
    {
        SectionType.Skills => Skills,
        SectionType.Experience => Experience,
        SectionType.Education => Education,
        SectionType.Certifications => Certifications,
        SectionType.Projects => Projects,
        SectionType.Languages => Languages,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown scoring section.")
    };
}

namespace BuildCv.Domain.Scoring;

public sealed record ScoringWeightsSnapshot
{
    public double Skills { get; }
    public double Experience { get; }
    public double Education { get; }
    public double Certifications { get; }
    public double Projects { get; }
    public int SchemaVersion { get; }

    private ScoringWeightsSnapshot(double skills, double experience, double education, double certifications, double projects, int schemaVersion)
    {
        Skills = skills;
        Experience = experience;
        Education = education;
        Certifications = certifications;
        Projects = projects;
        SchemaVersion = schemaVersion;
    }

    public static ScoringWeightsSnapshot Create(double skills, double experience, double education, double certifications, double projects, int schemaVersion = 1)
    {
        if (skills < 0 || experience < 0 || education < 0 || certifications < 0 || projects < 0)
            throw new ArgumentException("Weights must be non-negative.");
        var sum = skills + experience + education + certifications + projects;
        if (Math.Abs(sum - 1.0) > 0.0001)
            throw new ArgumentException($"Weights must sum to 1.0 (actual: {sum}).");
        if (schemaVersion < 1)
            throw new ArgumentException("SchemaVersion must be >= 1.", nameof(schemaVersion));
        return new ScoringWeightsSnapshot(skills, experience, education, certifications, projects, schemaVersion);
    }

    public static ScoringWeightsSnapshot Default() => Create(0.45, 0.20, 0.20, 0.10, 0.05);
}

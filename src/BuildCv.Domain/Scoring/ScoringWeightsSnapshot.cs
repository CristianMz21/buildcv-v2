namespace BuildCv.Domain.Scoring;

public sealed record ScoringWeightsSnapshot
{
    // Which WEIGHTING produced a set of numbers — not which shape they serialize in.
    //
    // It is 2 because Languages now carries weight and is now computed: a score explained by these
    // weights is NOT the score the five-section model produced for the same resume, and a row that
    // could not say which model explained it would make every historical comparison a lie.
    //
    // THIS IS THE ONE-WAY DOOR the v1 comment promised. Version 1's payload was rollback-safe: a
    // reader built before Languages existed skipped the unmapped member (System.Text.Json does that
    // by default), saw five weights summing to 1.0, and worked. That same old reader now sees five
    // weights summing to 0.90 and Create throws on the sum invariant — so every analysis row written
    // after this deploys is UNREADABLE to any build older than it, not merely differently explained.
    // There is no rolling back past the first write. Verified by reading FromJson's deserialization
    // path rather than assuming the default, and pinned by
    // ValueObjectConverterTests.ScoringWeights_AVersionOnePayloadNoLongerMatchesTodaysWeighting.
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

    // The redistribution: Education 0.20 → 0.10, Languages 0.00 → 0.10.
    //
    // WEIGHT AND SCORE ARRIVE TOGETHER, which is the only reason this is safe to do in one step. The
    // previous release shipped Languages shaped but unweighted precisely so that no window existed in
    // which a section carried weight that nothing computed — a 0.10 weight against a hard-coded 0.0
    // would have capped every candidate at 0.90 and taken up to ten points off everyone with an
    // education. This factory changes only alongside a Languages score the engine really produces.
    //
    // Scores DO move here, and that is the point rather than a regression: a candidate who speaks the
    // language a posting asks for gains up to ten points, and a candidate whose education was carrying
    // a fifth of their score now carries a tenth of it. SchemaVersion 2 is what keeps an old analysis
    // explainable under the model that produced it.
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

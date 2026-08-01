namespace BuildCv.Domain.Scoring;

public sealed record ScoringWeightsSnapshot
{
    // Which WEIGHTING produced a set of numbers — not which shape they serialize in.
    //
    // It stays at 1 while Languages carries weight 0.0, because the arithmetic is bit-for-bit the
    // five-section arithmetic that has always run: a score explained by these weights is explained by
    // the v1 weights. Bumping it here would claim a scoring model changed when none did, and would
    // put a discontinuity in every candidate's history for a serialization detail.
    //
    // It moves to 2 in the same commit that redistributes Education to Languages and starts computing
    // a real Languages score — the commit where the numbers genuinely change.
    //
    // THAT COMMIT IS A ONE-WAY DOOR, and this one is not. Today's payload is rollback-safe: a reader
    // built before Languages existed skips the unmapped member (System.Text.Json does that by
    // default), sees the same five weights summing to 1.0, and works. Once the redistribution ships,
    // the same old reader sees five weights summing to 0.90 and Create throws — every analysis row
    // written after the bump becomes unreadable, not just differently explained. Deploy that release
    // knowing there is no rolling back past the first write.
    public const int CurrentSchemaVersion = 1;

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

    // Languages ships SHAPED BUT UNWEIGHTED, and that is what makes adding it behaviour-neutral.
    //
    // Its weight is 0.0 and the engine hands it a 0.0 score, so it contributes nothing twice over and
    // the other five weights are untouched — every candidate's score is the number it was yesterday,
    // and no history shows a discontinuity. Weighting Languages before anything computes it would
    // have moved Education from 0.20 to 0.10 against a hard-coded zero: a maximum of 0.90 instead of
    // 1.00, and up to ten points off every candidate with an education. The redistribution belongs in
    // the commit that starts computing the score, so the weight and the score arrive together.
    public static ScoringWeightsSnapshot Default() => Create(0.45, 0.20, 0.20, 0.10, 0.05, 0.00);

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

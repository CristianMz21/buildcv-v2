namespace BuildCv.Domain.Scoring;

public sealed record ScoreBreakdown
{
    // Numeric order, which is the order Sections projects them in.
    private static readonly SectionType[] AllSections = Enum.GetValues<SectionType>();

    public double SkillsScore { get; }
    public double ExperienceScore { get; }
    public double EducationScore { get; }
    public double CertificationsScore { get; }
    public double ProjectsScore { get; }
    public double LanguagesScore { get; }
    public ScoringWeightsSnapshot Weights { get; }

    private ScoreBreakdown(
        double skillsScore,
        double experienceScore,
        double educationScore,
        double certificationsScore,
        double projectsScore,
        double languagesScore,
        ScoringWeightsSnapshot weights)
    {
        SkillsScore = skillsScore;
        ExperienceScore = experienceScore;
        EducationScore = educationScore;
        CertificationsScore = certificationsScore;
        ProjectsScore = projectsScore;
        LanguagesScore = languagesScore;
        Weights = weights;
    }

    public static ScoreBreakdown Create(
        double skillsScore,
        double experienceScore,
        double educationScore,
        double certificationsScore,
        double projectsScore,
        double languagesScore,
        ScoringWeightsSnapshot weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ValidateScore(skillsScore, nameof(skillsScore));
        ValidateScore(experienceScore, nameof(experienceScore));
        ValidateScore(educationScore, nameof(educationScore));
        ValidateScore(certificationsScore, nameof(certificationsScore));
        ValidateScore(projectsScore, nameof(projectsScore));
        ValidateScore(languagesScore, nameof(languagesScore));
        return new ScoreBreakdown(
            skillsScore, experienceScore, educationScore, certificationsScore, projectsScore, languagesScore, weights);
    }

    // FINITE first: `NaN < 0` and `NaN > 1` are both false, so a NaN score would pass the range check
    // and then poison WeightedTotal, every band, and the whole response — one unguarded division
    // upstream and the candidate is shown nothing at all.
    private static void ValidateScore(double score, string paramName)
    {
        if (!double.IsFinite(score))
            throw new ArgumentException("Score must be a finite number.", paramName);
        if (score < 0 || score > 1)
            throw new ArgumentException("Score must be between 0 and 1.", paramName);
    }

    public double WeightedTotal =>
        Weights.Skills * SkillsScore +
        Weights.Experience * ExperienceScore +
        Weights.Education * EducationScore +
        Weights.Certifications * CertificationsScore +
        Weights.Projects * ProjectsScore +
        Weights.Languages * LanguagesScore;

    // The six stored doubles paired with the weights they were counted under, so a caller never has to
    // pair a score with a weight by hand and cannot pair it with the wrong snapshot's.
    //
    // COMPUTED, and the persistence layer Ignores it. Left mapped, EF discovers SectionScore as an
    // entity type and the model build fails somewhere far from here.
    public IReadOnlyList<SectionScore> Sections =>
        [.. AllSections.Select(section => SectionScore.Create(section, ScoreFor(section), Weights.WeightFor(section)))];

    // THE enum-to-column switch, deliberately the only one. Every consumer that wants "the score for
    // this section" reads through here, so adding a SectionType member without a column to back it
    // fails loudly in one place instead of quietly reading zero in several.
    public double ScoreFor(SectionType section) => section switch
    {
        SectionType.Skills => SkillsScore,
        SectionType.Experience => ExperienceScore,
        SectionType.Education => EducationScore,
        SectionType.Certifications => CertificationsScore,
        SectionType.Projects => ProjectsScore,
        SectionType.Languages => LanguagesScore,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown scoring section.")
    };
}

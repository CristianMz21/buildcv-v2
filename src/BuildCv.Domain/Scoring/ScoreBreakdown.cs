namespace BuildCv.Domain.Scoring;

public sealed record ScoreBreakdown
{
    public double SkillsScore { get; }
    public double ExperienceScore { get; }
    public double EducationScore { get; }
    public double CertificationsScore { get; }
    public double ProjectsScore { get; }
    public ScoringWeightsSnapshot Weights { get; }

    private ScoreBreakdown(
        double skillsScore,
        double experienceScore,
        double educationScore,
        double certificationsScore,
        double projectsScore,
        ScoringWeightsSnapshot weights)
    {
        SkillsScore = skillsScore;
        ExperienceScore = experienceScore;
        EducationScore = educationScore;
        CertificationsScore = certificationsScore;
        ProjectsScore = projectsScore;
        Weights = weights;
    }

    public static ScoreBreakdown Create(
        double skillsScore,
        double experienceScore,
        double educationScore,
        double certificationsScore,
        double projectsScore,
        ScoringWeightsSnapshot weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ValidateScore(skillsScore, nameof(skillsScore));
        ValidateScore(experienceScore, nameof(experienceScore));
        ValidateScore(educationScore, nameof(educationScore));
        ValidateScore(certificationsScore, nameof(certificationsScore));
        ValidateScore(projectsScore, nameof(projectsScore));
        return new ScoreBreakdown(skillsScore, experienceScore, educationScore, certificationsScore, projectsScore, weights);
    }

    private static void ValidateScore(double score, string paramName)
    {
        if (score < 0 || score > 1)
            throw new ArgumentException("Score must be between 0 and 1.", paramName);
    }

    public double WeightedTotal =>
        Weights.Skills * SkillsScore +
        Weights.Experience * ExperienceScore +
        Weights.Education * EducationScore +
        Weights.Certifications * CertificationsScore +
        Weights.Projects * ProjectsScore;
}

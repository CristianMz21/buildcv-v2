namespace BuildCv.Application.Scoring;

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

public sealed class ScoringEngine : IScoringEngine
{
    public ScoreBreakdown Score(Resume resume, JobPosting jobPosting, DateOnly referenceDate)
    {
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(jobPosting);

        var skillsScore = ComputeSkillsScore(resume, jobPosting);
        var experienceScore = ComputeExperienceScore(resume, referenceDate);
        var educationScore = ComputeEducationScore(resume);
        var certificationsScore = ComputeCertificationsScore(resume, referenceDate);
        var projectsScore = ComputeProjectsScore(resume);

        return ScoreBreakdown.Create(
            skillsScore,
            experienceScore,
            educationScore,
            certificationsScore,
            projectsScore,
            // Languages is shaped but not yet computed: the engine still scores exactly the five
            // sections it always has, and a 0.0 here says so honestly rather than inventing a number.
            0.0,
            ScoringWeightsSnapshot.Default());
    }

    private static double ComputeSkillsScore(Resume resume, JobPosting jobPosting)
    {
        if (jobPosting.Requirements.Count == 0)
            return 0.5;

        double matchedWeight = 0;
        double totalWeight = 0;

        foreach (var requirement in jobPosting.Requirements)
        {
            var weight = requirement.Priority == RequirementPriority.MustHave ? 1.0 : 0.5;
            totalWeight += weight;
            if (IsSatisfiedBy(requirement, resume))
                matchedWeight += weight;
        }

        return Math.Clamp(matchedWeight / totalWeight, 0.0, 1.0);
    }

    private static bool IsSatisfiedBy(JobRequirement requirement, Resume resume) =>
        resume.Skills.Any(s => s.Name.Name.Equals(requirement.Skill.Name, StringComparison.OrdinalIgnoreCase))
        || resume.Skills.Any(s => s.Keywords.Any(k => k.Equals(requirement.Skill.Name, StringComparison.OrdinalIgnoreCase)))
        || resume.Projects.Any(p => p.Technologies.Any(t => t.Name.Equals(requirement.Skill.Name, StringComparison.OrdinalIgnoreCase)));

    private static double ComputeExperienceScore(Resume resume, DateOnly referenceDate)
    {
        var totalDays = resume.Experiences
            .Where(e => e.Type == ExperienceType.Professional)
            .Sum(e => e.Period.DurationInDays(referenceDate));

        return Math.Clamp(totalDays / (365.0 * 5), 0.0, 1.0);
    }

    private static double ComputeEducationScore(Resume resume)
    {
        if (resume.Educations.Count == 0)
            return 0.0;

        return resume.Educations.Any(e => !string.IsNullOrWhiteSpace(e.Degree)) ? 1.0 : 0.7;
    }

    private static double ComputeCertificationsScore(Resume resume, DateOnly referenceDate)
    {
        var validCount = resume.Certificates.Count(c =>
            c.ValidityPeriod is null
            || c.ValidityPeriod.IsCurrent
            || c.ValidityPeriod.End >= referenceDate);

        return Math.Clamp(validCount / 3.0, 0.0, 1.0);
    }

    private static double ComputeProjectsScore(Resume resume)
    {
        var count = resume.Projects.Count(p => p.Technologies.Count > 0 || p.Highlights.Count > 0);
        return Math.Clamp(count / 3.0, 0.0, 1.0);
    }
}

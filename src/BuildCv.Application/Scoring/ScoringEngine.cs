namespace BuildCv.Application.Scoring;

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

// A pure function of (resume, posting, referenceDate). Registered as a singleton, so it holds no
// state and neither does anything it calls.
//
// Every formula lives in ScoringRules rather than here, because RecommendationBuilder has to evaluate
// the same formulas to say what acting on a gap is worth.
public sealed class ScoringEngine : IScoringEngine
{
    public ScoreBreakdown Score(Resume resume, JobPosting jobPosting, DateOnly referenceDate)
    {
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(jobPosting);

        var (matchedWeight, totalWeight) = ScoringRules.SkillWeights(resume, jobPosting);

        return ScoreBreakdown.Create(
            ScoringRules.SkillsScore(matchedWeight, totalWeight),
            ScoringRules.ExperienceScore(ScoringRules.ProfessionalDays(resume, referenceDate)),
            ScoringRules.EducationScore(resume),
            ScoringRules.CertificationsScore(ScoringRules.ValidCertificateCount(resume, referenceDate)),
            ScoringRules.ProjectsScore(ScoringRules.QualifyingProjectCount(resume)),
            ScoringRules.LanguagesScore(
                ScoringRules.SatisfiedLanguageCount(resume, jobPosting),
                jobPosting.LanguageRequirements.Count),
            ScoringWeightsSnapshot.Default());
    }
}

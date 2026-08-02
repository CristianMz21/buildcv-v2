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
    public ScoreResult Score(Resume resume, JobPosting jobPosting, DateOnly referenceDate)
    {
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(jobPosting);

        var breakdown = BuildBreakdown(resume, jobPosting, referenceDate);

        // The advice is derived from the breakdown that was just produced, not recomputed from the
        // inputs, so an Impact can only ever describe the score this same call returns.
        return ScoreResult.Create(
            breakdown, RecommendationBuilder.Build(resume, jobPosting, breakdown, referenceDate));
    }

    private static ScoreBreakdown BuildBreakdown(Resume resume, JobPosting jobPosting, DateOnly referenceDate)
    {
        var (matchedWeight, totalWeight) = ScoringRules.SkillWeights(resume, jobPosting);
        var languageRequirementCount = jobPosting.LanguageRequirements.Count;

        // The weights this posting is scored under, not the defaults. A section the posting asks nothing
        // of is renormalized out, so the ceiling is 1.00 for every posting.
        //
        // THE RENORMALIZED SET IS WHAT GETS PERSISTED, because ScoreBreakdown carries it into Analysis.
        // Scoring under one set of weights and storing another would leave every historical row holding
        // numbers that do not add up — the snapshot exists precisely so a past analysis stays
        // self-explaining and arithmetically reproducible from the row alone.
        var weights = ScoringWeightsSnapshot.Default()
            .RenormalizedTo(ScoringRules.ApplicableSections(totalWeight, languageRequirementCount));

        return ScoreBreakdown.Create(
            ScoringRules.SkillsScore(matchedWeight, totalWeight),
            ScoringRules.ExperienceScore(ScoringRules.ProfessionalDays(resume, referenceDate)),
            ScoringRules.EducationScore(resume),
            ScoringRules.CertificationsScore(ScoringRules.ValidCertificateCount(resume, referenceDate)),
            ScoringRules.ProjectsScore(ScoringRules.QualifyingProjectCount(resume)),
            ScoringRules.LanguagesScore(
                ScoringRules.SatisfiedLanguageCount(resume, jobPosting),
                languageRequirementCount),
            weights);
    }
}

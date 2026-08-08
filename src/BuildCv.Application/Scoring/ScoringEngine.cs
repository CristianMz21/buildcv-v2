namespace BuildCv.Application.Scoring;

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

// A pure function of (resume, posting, referenceDate, lexicon). Registered as a singleton, so it holds
// no state and neither does anything it calls.
//
// Every formula lives in ScoringRules rather than here, because RecommendationBuilder has to evaluate
// the same formulas to say what acting on a gap is worth.
//
// THE LEXICON IS A CONSTRUCTOR ARGUMENT WITH NO DEFAULT, and that is worth a sentence because a default
// was available and is wrong. An optional parameter falling back to an empty lexicon would mean a host
// that forgot the registration still scored -- correctly by yesterday's rules, silently by nobody's
// intent, and stamped with the SchemaVersion that says the lexicon was consulted. A missing registration
// is a startup failure instead.
public sealed class ScoringEngine : IScoringEngine
{
    private readonly ISkillLexicon _skillLexicon;

    public ScoringEngine(ISkillLexicon skillLexicon)
    {
        ArgumentNullException.ThrowIfNull(skillLexicon);
        _skillLexicon = skillLexicon;
    }

    public ScoreResult Score(Resume resume, JobPosting jobPosting, DateOnly referenceDate)
    {
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(jobPosting);

        var breakdown = BuildBreakdown(resume, jobPosting, referenceDate, _skillLexicon);

        // The advice is derived from the breakdown that was just produced, not recomputed from the
        // inputs, so an Impact can only ever describe the score this same call returns. The same lexicon
        // instance goes to both, so the advice cannot disagree with the score about what "having this
        // skill" means.
        return ScoreResult.Create(
            breakdown,
            RecommendationBuilder.Build(resume, jobPosting, breakdown, referenceDate, _skillLexicon));
    }

    private static ScoreBreakdown BuildBreakdown(
        Resume resume, JobPosting jobPosting, DateOnly referenceDate, ISkillLexicon skillLexicon)
    {
        var (matchedWeight, totalWeight) = ScoringRules.SkillWeights(resume, jobPosting, skillLexicon);
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

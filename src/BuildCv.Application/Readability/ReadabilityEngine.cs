namespace BuildCv.Application.Readability;

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;

// A pure function of (resume, referenceDate). Registered as a singleton, so it holds no state and
// neither does anything it calls.
//
// It takes NO CONSTRUCTOR DEPENDENCIES, unlike ScoringEngine's required ISkillLexicon, and the reason is
// that no readability rule consults data outside the resume: the action-verb vocabulary is a closed set
// in ActionVerbs, and there is nothing to configure or to forget to register.
//
// Every formula lives in ReadabilityRules rather than here, because ReadabilityRecommendationBuilder has
// to evaluate the same formulas to say what acting on a gap is worth.
public sealed class ReadabilityEngine : IReadabilityEngine
{
    public ReadabilityResult Evaluate(Resume resume, DateOnly referenceDate)
    {
        ArgumentNullException.ThrowIfNull(resume);

        var breakdown = BuildBreakdown(resume, referenceDate);

        // The advice is derived from the breakdown that was just produced, not recomputed from the
        // inputs, so an Impact can only ever describe the score this same call returns.
        return ReadabilityResult.Create(
            breakdown, ReadabilityRecommendationBuilder.Build(resume, breakdown, referenceDate));
    }

    private static ReadabilityBreakdown BuildBreakdown(Resume resume, DateOnly referenceDate)
    {
        var (quantified, actionLed, totalHighlights) = ReadabilityRules.HighlightCounts(resume);
        var signals = resume.ImportSignals;

        // THE SCORE AND THE APPLICABILITY MOVE TOGETHER, and nothing here may separate them. A resume
        // with signals gets its ATS section measured AND weighted; one without gets neither, the section
        // is renormalized out, and the remaining four still total 1.0. Turning applicability on while the
        // score stayed a hard zero would weight 0.10 against 0.0 and cap every importer at 0.90 -- ten
        // points off, for a question the product then never asks -- which is the failure
        // ReadabilityWeightsSnapshot.RenormalizedTo's remark describes. Both branches below read the same
        // `signals`, so there is no second condition to get out of step with this one.
        var weights = ReadabilityWeightsSnapshot.Default()
            .RenormalizedTo(ReadabilityRules.ApplicableSections(hasImportSignals: signals is not null));

        // NotApplicableScore when there are no signals, exactly as the other two conditional sections do
        // it: nothing was measured, so the honest answer is zero, and the weight of zero beside it is
        // what keeps that zero out of the total.
        var atsParseability = ReadabilityRules.NotApplicableScore;
        if (signals is not null)
        {
            var (met, measurable) = ReadabilityRules.AtsSignalCounts(signals);
            atsParseability = ReadabilityRules.AtsParseabilityScore(met, measurable);
        }

        return ReadabilityBreakdown.Create(
            ReadabilityRules.CompletenessScore(ReadabilityRules.PresentSectionCount(resume)),
            ReadabilityRules.ContactScore(ReadabilityRules.RecordedContactChannelCount(resume)),
            ReadabilityRules.AchievementsScore(quantified, actionLed, totalHighlights),
            ReadabilityRules.ChronologyScore(
                ReadabilityRules.ContinuousEntryCount(resume, referenceDate),
                resume.Experiences.Count),
            atsParseability,
            weights);
    }
}

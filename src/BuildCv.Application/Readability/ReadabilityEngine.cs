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

        // FALSE, AND IT IS THE ONE LINE ROADMAP T3.5 REPLACES: `resume.ImportSignals is not null`. No
        // resume this build can load carries import signals -- Resume has no such member yet -- so
        // AtsParseability is renormalized out of every report, its weight reads 0 on the wire, and the
        // remaining four still total 1.0. Written as a named argument rather than inlined so the
        // substitution is one word in one place.
        var weights = ReadabilityWeightsSnapshot.Default()
            .RenormalizedTo(ReadabilityRules.ApplicableSections(hasImportSignals: false));

        return ReadabilityBreakdown.Create(
            ReadabilityRules.CompletenessScore(ReadabilityRules.PresentSectionCount(resume)),
            ReadabilityRules.ContactScore(ReadabilityRules.RecordedContactChannelCount(resume)),
            ReadabilityRules.AchievementsScore(quantified, actionLed, totalHighlights),
            ReadabilityRules.ChronologyScore(
                ReadabilityRules.ContinuousEntryCount(resume, referenceDate),
                resume.Experiences.Count),
            ReadabilityRules.AtsParseabilityScore(),
            weights);
    }
}

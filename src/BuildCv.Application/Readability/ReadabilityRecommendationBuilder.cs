namespace BuildCv.Application.Readability;

using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

// Deterministic advice about one resume, with no posting anywhere in it.
//
// A static class with per-section generator methods rather than an IReadabilityRule interface with DI
// registration: an interface would add a registration surface, an ordering question and a configuration
// nobody asked for, and would stop the whole rule set being readable from one file. Static also makes
// "stateless" unforgeable, which matters because ReadabilityEngine is a singleton.
//
// IMPACT IS DERIVED, NEVER ASSIGNED.
//
//     Impact = the increase in WeightedTotal this report would gain if this advice were fully acted on.
//
// Every rule computes it the same way: take the section score the breakdown already holds, evaluate the
// SAME ReadabilityRules formula the engine used with exactly one gap closed, and multiply the difference
// by that section's weight. Nothing here states a number a reader could not reproduce by re-evaluating,
// which is the property ActingOnAReadabilityRecommendationTests measures rule by rule.
//
// A number that came from someone's intuition would be worse than no number at all: it gives a candidate
// false precision they cannot verify, about the one thing they are here to improve.
//
// EMIT NOTHING A CANDIDATE CANNOT ACT ON. Two omissions carry that rule and both are deliberate. There
// is no "your career is too short" advice, because that is a fact about time. And a resume with no
// experience entries gets NO achievements advice at all -- "add a bullet point" names an edit to a role
// that does not exist -- so that section's whole weight sits unadvised until the Chronology rule's
// advice has been acted on. Advice that appears once it can be followed is better than advice that
// cannot, and the section still scores zero meanwhile: not advising it is not the same as excusing it.
internal static class ReadabilityRecommendationBuilder
{
    // Ten is a reading limit, not a scoring one. Past it a candidate is being handed a backlog rather
    // than advice, and the total order guarantees the ten kept are the ten worth most.
    internal const int MaxRecommendations = 10;

    // The two thresholds Priority is a pure function of, and the same two the scoring builder uses --
    // both engines put advice on the same 0..1 Impact scale, so a candidate reading one to-do list must
    // not find "Critical" meaning two different magnitudes. One rule in one place, so the number and the
    // label can never disagree: hand-assigned per-rule priorities are how a rule set drifts into
    // "everything is Critical".
    private const double CriticalImpact = 0.10;
    private const double ImportantImpact = 0.03;

    // Concatenated in ReadabilitySectionType order, then sorted. The concatenation order is not the
    // output order and never has to be, but a fixed one keeps the diff of "what does this emit" readable.
    internal static IReadOnlyList<ReadabilityRecommendation> Build(
        Resume resume, ReadabilityBreakdown breakdown, DateOnly referenceDate)
    {
        List<ReadabilityRecommendation> found =
        [
            .. ForCompleteness(resume, breakdown),
            .. ForContact(resume, breakdown),
            .. ForAchievements(resume, breakdown),
            .. ForChronology(resume, breakdown, referenceDate),
        ];

        return [.. ReadabilityRecommendationOrder.Sort(found).Take(MaxRecommendations)];
    }

    // One recommendation per missing section, never one lumped "fill in the gaps": each names a
    // different afternoon's work, and the impacts are equal only because the three terms are.
    //
    // Impact is what adding THIS section alone is worth -- evaluated through the engine's own
    // CompletenessScore rather than restated as 1/3 of the weight, so a change to the formula or to the
    // expected section count cannot leave the advice quoting the old one.
    private static IEnumerable<ReadabilityRecommendation> ForCompleteness(
        Resume resume, ReadabilityBreakdown breakdown)
    {
        var present = ReadabilityRules.PresentSectionCount(resume);
        var current = breakdown.CompletenessScore;
        var impact = breakdown.Weights.Completeness
            * (ReadabilityRules.CompletenessScore(present + 1) - current);

        if (!ReadabilityRules.RecordsEducation(resume))
        {
            yield return Advice(
                ReadabilitySectionType.Completeness,
                ReadabilityRecommendationKind.NoEducationRecorded,
                "Add an education section, including where you studied and when: an ATS looks for one and "
                + "this resume has none.",
                impact);
        }

        if (!ReadabilityRules.RecordsSkills(resume))
        {
            yield return Advice(
                ReadabilitySectionType.Completeness,
                ReadabilityRecommendationKind.NoSkillsRecorded,
                "Add a skills section listing the technologies you work with: this resume records none, "
                + "and a keyword an ATS cannot find is a keyword you do not have.",
                impact);
        }

        if (!ReadabilityRules.RecordsSummary(resume))
        {
            yield return Advice(
                ReadabilitySectionType.Completeness,
                ReadabilityRecommendationKind.NoProfessionalSummary,
                "Write a short professional summary at the top of your resume: it is the first thing a "
                + "recruiter reads and this resume has none.",
                impact);
        }
    }

    private static IEnumerable<ReadabilityRecommendation> ForContact(
        Resume resume, ReadabilityBreakdown breakdown)
    {
        var recorded = ReadabilityRules.RecordedContactChannelCount(resume);
        var current = breakdown.ContactScore;
        var impact = breakdown.Weights.Contact * (ReadabilityRules.ContactScore(recorded + 1) - current);

        if (!ReadabilityRules.RecordsPhoneNumber(resume))
        {
            yield return Advice(
                ReadabilitySectionType.Contact,
                ReadabilityRecommendationKind.NoPhoneNumber,
                "Add a phone number in international format (+54 11 ...): a recruiter who cannot call you "
                + "has one fewer way to reach you.",
                impact);
        }

        if (!ReadabilityRules.RecordsLocation(resume))
        {
            yield return Advice(
                ReadabilitySectionType.Contact,
                ReadabilityRecommendationKind.NoLocation,
                "Add your location: it is one of the first fields an ATS filters on, and this resume "
                + "records none.",
                impact);
        }

        if (!ReadabilityRules.RecordsOnlinePresence(resume))
        {
            yield return Advice(
                ReadabilitySectionType.Contact,
                ReadabilityRecommendationKind.NoOnlinePresence,
                "Add a website or one online profile: this resume names neither, so a recruiter has "
                + "nothing to follow up.",
                impact);
        }
    }

    // Three gaps, three actions, and each impact is what closing exactly that one is worth.
    //
    // The two per-bullet-point rules emit ONE recommendation each, not one per offending line. The
    // advice is "do this to one more of them", and its impact is what one more is worth -- emitting five
    // copies of the same sentence with the same number would be five chances to act on one piece of
    // advice, and only the first would pay what it promised.
    //
    // Nothing is emitted when the resume records no experience: there is no bullet point to write and no
    // role to write it under.
    private static IEnumerable<ReadabilityRecommendation> ForAchievements(
        Resume resume, ReadabilityBreakdown breakdown)
    {
        if (resume.Experiences.Count == 0)
            yield break;

        var (quantified, actionLed, total) = ReadabilityRules.HighlightCounts(resume);
        var current = breakdown.AchievementsScore;

        if (total == 0)
        {
            // The advice names BOTH halves of the section, so the impact it promises is the one the
            // instruction actually pays: a first bullet point that neither states a number nor starts
            // with a verb takes the section to 0.0, not to its ceiling.
            yield return Advice(
                ReadabilitySectionType.Achievements,
                ReadabilityRecommendationKind.NoExperienceHighlights,
                "Add at least one bullet point under your experience, starting with an action verb and "
                + "stating a number (\"Reduced deploy time by 40%\"): this resume lists roles but says "
                + "nothing about what you achieved in them.",
                breakdown.Weights.Achievements * (ReadabilityRules.AchievementsScore(1, 1, 1) - current));
            yield break;
        }

        if (quantified < total)
        {
            yield return Advice(
                ReadabilitySectionType.Achievements,
                ReadabilityRecommendationKind.HighlightWithoutANumber,
                $"Put a number in one more of your experience bullet points -- an amount, a percentage or "
                + $"a count: {quantified} of {total} state one today.",
                breakdown.Weights.Achievements
                    * (ReadabilityRules.AchievementsScore(quantified + 1, actionLed, total) - current));
        }

        if (actionLed < total)
        {
            yield return Advice(
                ReadabilitySectionType.Achievements,
                ReadabilityRecommendationKind.HighlightWithoutAnActionVerb,
                $"Start one more of your experience bullet points with an action verb (\"Led\", "
                + $"\"Reduced\", \"Lideré\", \"Migré\"): {actionLed} of {total} do today.",
                breakdown.Weights.Achievements
                    * (ReadabilityRules.AchievementsScore(quantified, actionLed + 1, total) - current));
        }
    }

    // The section that owns the work history, including whether there is one at all -- see the
    // disjointness note on ReadabilityRules for why that heading is not Completeness's.
    private static IEnumerable<ReadabilityRecommendation> ForChronology(
        Resume resume, ReadabilityBreakdown breakdown, DateOnly referenceDate)
    {
        var count = resume.Experiences.Count;
        var current = breakdown.ChronologyScore;

        if (count == 0)
        {
            // One entry is enough to give the section its ceiling: a single role has no transition and
            // therefore no break, so the honest impact is the whole of what the section is worth.
            yield return Advice(
                ReadabilitySectionType.Chronology,
                ReadabilityRecommendationKind.NoExperienceRecorded,
                "Add your work experience with a start and end date on every entry: this resume records "
                + "none, so there is no timeline for a recruiter to read.",
                breakdown.Weights.Chronology * (ReadabilityRules.ChronologyScore(1, 1) - current));
            yield break;
        }

        var continuous = ReadabilityRules.ContinuousEntryCount(resume, referenceDate);

        // One recommendation per gap, because a candidate deciding where to spend an afternoon needs
        // them apart, and because the advice has to name WHICH break it means.
        //
        // Closing one gap by adding an entry that covers it changes BOTH terms of the formula: the entry
        // count goes up by one, and the one broken transition is replaced by two unbroken ones, so the
        // numerator gains two. Evaluated through the engine's own ChronologyScore with exactly those two
        // substitutions rather than restated as 1/n, because 1/n is what the arithmetic would be if the
        // denominator stood still -- and it does not.
        foreach (var gap in ReadabilityRules.EmploymentGaps(resume, referenceDate))
        {
            yield return Advice(
                ReadabilitySectionType.Chronology,
                ReadabilityRecommendationKind.UnexplainedEmploymentGap,
                $"Add an entry covering the {gap.Months}-month gap before '{gap.Following.Position}': an "
                + "unexplained break is the first thing a recruiter asks about, and a course, a "
                + "contract or volunteering all count.",
                breakdown.Weights.Chronology
                    * (ReadabilityRules.ChronologyScore(continuous + 2, count + 1) - current));
        }
    }

    private static ReadabilityRecommendation Advice(
        ReadabilitySectionType section,
        ReadabilityRecommendationKind kind,
        string message,
        double impact) =>
        ReadabilityRecommendation.Create(
            section,
            PriorityFor(impact),
            kind,
            message,
            // Derived impacts are non-negative by construction -- every target above is the same
            // monotone formula evaluated with one more unit of the thing it counts -- and no weight
            // exceeds 1.0, so the product stays inside the unit interval
            // ReadabilityRecommendation.Create demands. The clamp is here so a future rule that got the
            // direction wrong fails its own impact test rather than throwing
            // InvalidRecommendationException from inside a readability request.
            Math.Clamp(impact, 0.0, 1.0));

    // Priority is a pure function of Impact and nothing else may set it, so the label can never drift
    // from the number beside it.
    //
    // There is no equivalent of the scoring builder's must-have gate, and its absence is the point: that
    // gate exists because a posting SCREENS on a requirement whatever the arithmetic pays, and there is
    // no posting here. Every readability gap is worth exactly what closing it pays.
    private static RecommendationPriority PriorityFor(double impact) =>
        impact >= CriticalImpact ? RecommendationPriority.Critical
        : impact >= ImportantImpact ? RecommendationPriority.Important
        : RecommendationPriority.NiceToHave;
}

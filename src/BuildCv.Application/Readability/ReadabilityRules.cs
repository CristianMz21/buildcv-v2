namespace BuildCv.Application.Readability;

using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;

// Every readability section formula and every predicate it is built from, in ONE place, because two
// consumers read them and a disagreement between those two consumers is invisible.
//
// ReadabilityEngine turns them into the five section scores. ReadabilityRecommendationBuilder evaluates
// the SAME formula a second time with one gap closed, and reports the difference as
// ReadabilityRecommendation.Impact -- so the number a candidate is shown is "the score you would get",
// not an estimate of it.
//
// EVERY SECTION IS A FUNCTION OF THE RESUME ALONE. No posting is loaded, none exists in the system on
// the path that reaches here, and nothing below takes one. That is the whole reason this engine exists:
// a candidate gets an answer the moment they upload a CV.
//
// THE SECTIONS READ DISJOINT INPUTS, and that is a design constraint rather than an accident. Impact is
// defined as the increase in the TOTAL from acting on one recommendation, and it is computed as one
// section's weight times that section's delta -- which is only the same number when the fix moves
// exactly one section. So the work-history SECTION HEADING lives in Chronology (which already reads the
// entries) and not in Completeness: counted in both, adding a first experience would move two sections
// and every Impact naming it would understate what it paid.
//
//   Completeness  reads Educations, Skills and ContactInformation.Summary
//   Contact       reads ContactInformation.PhoneNumber, .Location, .Website and .Profiles
//   Achievements  reads Experience.Highlights
//   Chronology    reads Experience.Period and the number of experience entries
//
// Static and pure: ReadabilityEngine is registered as a singleton and shared across every request.
internal static class ReadabilityRules
{
    // What a section scores when it cannot be measured at all. NOTHING WAS MEASURED, so the honest answer
    // is zero and the weight is zero beside it -- the section is renormalized out of the total by
    // ReadabilityWeightsSnapshot.RenormalizedTo and cannot move it in either direction.
    internal const double NotApplicableScore = 0.0;

    // The four sections computable from any resume this build can load. AtsParseability is absent, and
    // ApplicableSections is the only place that can add it.
    private static readonly ReadabilitySectionType[] AlwaysApplicable =
    [
        ReadabilitySectionType.Completeness,
        ReadabilitySectionType.Contact,
        ReadabilitySectionType.Achievements,
        ReadabilitySectionType.Chronology,
    ];

    // Which sections could be measured for this resume. The weights are renormalized across exactly
    // these, so the ceiling is 1.00 whether or not the ATS evidence exists.
    //
    // The parameter is a BOOL rather than a Resume because there is nothing on a Resume to read yet:
    // roadmap T3.5 adds the signed import-signals token and the nullable owned ImportSignals value, and
    // this becomes `resume.ImportSignals is not null` at its one call site in ReadabilityEngine. Keeping
    // the renormalization decision in a parameter means the mechanism is exercised in BOTH directions
    // today -- a rule that only ever answered one way would be a guarantee nothing could falsify.
    internal static IReadOnlyList<ReadabilitySectionType> ApplicableSections(bool hasImportSignals) =>
        hasImportSignals
            ? [.. AlwaysApplicable, ReadabilitySectionType.AtsParseability]
            : AlwaysApplicable;

    // COMPLETENESS -- the share of the three sections an ATS expects to find beside the work history:
    // education, a skills list, and a professional summary.
    //
    // The work-history heading is Chronology's, for the disjointness reason on this class. Name and
    // email are deliberately NOT counted here or in Contact: ContactInformation requires both, so no
    // resume can be missing them, and a term that is always 1 measures nothing while quietly making
    // every other term worth less.
    internal const double ExpectedSectionCount = 3.0;

    internal static double CompletenessScore(double presentSections) =>
        Math.Clamp(presentSections / ExpectedSectionCount, 0.0, 1.0);

    internal static int PresentSectionCount(Resume resume) =>
        (RecordsEducation(resume) ? 1 : 0)
        + (RecordsSkills(resume) ? 1 : 0)
        + (RecordsSummary(resume) ? 1 : 0);

    internal static bool RecordsEducation(Resume resume) => resume.Educations.Count > 0;

    internal static bool RecordsSkills(Resume resume) => resume.Skills.Count > 0;

    internal static bool RecordsSummary(Resume resume) =>
        !string.IsNullOrWhiteSpace(resume.ContactInformation.Summary);

    // CONTACT -- the share of the three optional ways to reach a candidate that this resume records: a
    // phone number, a location, and one online presence (a website or a profile).
    //
    // The two required fields are excluded for the reason stated above. A website and a profile are one
    // channel rather than two: they answer the same recruiter question, and counting them separately
    // would tell a candidate who linked their GitHub that they are still one third short.
    internal const double ExpectedContactChannelCount = 3.0;

    internal static double ContactScore(double recordedChannels) =>
        Math.Clamp(recordedChannels / ExpectedContactChannelCount, 0.0, 1.0);

    internal static int RecordedContactChannelCount(Resume resume) =>
        (RecordsPhoneNumber(resume) ? 1 : 0)
        + (RecordsLocation(resume) ? 1 : 0)
        + (RecordsOnlinePresence(resume) ? 1 : 0);

    internal static bool RecordsPhoneNumber(Resume resume) => resume.ContactInformation.PhoneNumber is not null;

    internal static bool RecordsLocation(Resume resume) =>
        !string.IsNullOrWhiteSpace(resume.ContactInformation.Location);

    internal static bool RecordsOnlinePresence(Resume resume) =>
        resume.ContactInformation.Website is not null || resume.ContactInformation.Profiles.Count > 0;

    // ACHIEVEMENTS -- half the section for the share of experience bullet points that state a number,
    // and half for the share that begin with an action verb.
    //
    // Experience.Highlights is where the achievements live and NOTHING ELSE IN THIS CODEBASE READS IT,
    // which is what makes this the richest untapped signal on the aggregate. It is encrypted at rest;
    // the prohibition is on LINQ over the column, and this reads it in memory off a materialized
    // aggregate, so no reclassification is needed.
    //
    // No highlights at all scores zero rather than renormalizing out. A resume that lists roles and says
    // nothing about them HAS been measured -- the answer is that it states no achievements -- and
    // renormalizing would hand the candidate a higher total for writing less.
    internal static double AchievementsScore(double quantified, double actionLed, double total) =>
        total <= 0.0
            ? NotApplicableScore
            : (Math.Clamp(quantified / total, 0.0, 1.0) + Math.Clamp(actionLed / total, 0.0, 1.0)) / 2.0;

    internal static (int Quantified, int ActionLed, int Total) HighlightCounts(Resume resume)
    {
        var quantified = 0;
        var actionLed = 0;
        var total = 0;

        foreach (var highlight in resume.Experiences.SelectMany(experience => experience.Highlights))
        {
            total++;
            if (StatesANumber(highlight))
                quantified++;
            if (ActionVerbs.StartsWithAnActionVerb(highlight))
                actionLed++;
        }

        return (quantified, actionLed, total);
    }

    // ANY DIGIT, and the looseness is deliberate rather than unnoticed. A stricter rule -- ignore
    // four-digit years, require a unit beside the number -- reads as more accurate and is the kind of
    // clever this engine refuses: it cannot be stated in one sentence, so a candidate could not check
    // it, and every extra clause is another way for the advice to promise a gain the rule then declines
    // to pay. The cost is that "Migrated 3 services" and "Joined in 2019" both count.
    internal static bool StatesANumber(string highlight) => highlight.Any(char.IsDigit);

    // CHRONOLOGY -- the share of experience entries that continue the timeline without an unexplained
    // break: the first entry always counts, and each later one counts when it starts within six months
    // of the end of everything before it.
    //
    // "Everything before it", not "the previous entry", so two roles held at once cannot manufacture a
    // gap: a five-year role running alongside a one-year one still covers the years after that shorter
    // one ended. That is also the whole of what this rule has to say about overlaps -- an overlap is
    // never a break, and "too many concurrent roles" is a judgement about a career rather than a fact
    // about a document.
    //
    // INVERTED RANGES ARE NOT CHECKED, because they cannot exist: DateRange.Create rejects an end before
    // its start with InvalidDateRangeException, so no materialized aggregate can hold one. A rule for it
    // would be a branch nothing can reach and a test that cannot fail.
    //
    // No experience entries scores zero: a resume with no work history has no timeline to read, and the
    // fix is the NoExperienceRecorded advice this section emits.
    internal const int MaxGapDays = 183;

    internal static double ChronologyScore(double continuousEntries, double experienceCount) =>
        experienceCount <= 0.0
            ? NotApplicableScore
            : Math.Clamp(continuousEntries / experienceCount, 0.0, 1.0);

    // The count the formula's numerator wants: entries, minus the ones that open after a break.
    internal static int ContinuousEntryCount(Resume resume, DateOnly referenceDate) =>
        resume.Experiences.Count == 0
            ? 0
            : resume.Experiences.Count - EmploymentGaps(resume, referenceDate).Count;

    internal static IReadOnlyList<EmploymentGap> EmploymentGaps(Resume resume, DateOnly referenceDate)
    {
        // Ordered by start, then by end, so the walk below reads the timeline forwards. The aggregate's
        // own list order is insertion order and says nothing about time.
        var ordered = resume.Experiences
            .OrderBy(experience => experience.Period.Start)
            .ThenBy(experience => experience.Period.End ?? referenceDate)
            .ToList();

        List<EmploymentGap> gaps = [];
        if (ordered.Count == 0)
            return gaps;

        // An open-ended entry runs to the reference date, which is the same reading ScoringRules gives
        // DateRange.DurationInDays: "Present" means today, so a current role covers up to today.
        var coverageEnd = ordered[0].Period.End ?? referenceDate;

        for (var index = 1; index < ordered.Count; index++)
        {
            var entry = ordered[index];
            var gapDays = entry.Period.Start.DayNumber - coverageEnd.DayNumber;
            if (gapDays > MaxGapDays)
                gaps.Add(new EmploymentGap(gapDays, entry));

            var entryEnd = entry.Period.End ?? referenceDate;
            if (entryEnd > coverageEnd)
                coverageEnd = entryEnd;
        }

        return gaps;
    }

    // ATS PARSEABILITY -- what the uploaded document looked like to a parser.
    //
    // IT SCORES NOTHING AND APPLIES TO NOTHING TODAY, and those two facts must move together. Roadmap
    // T3.5 is what supplies the evidence: a closed ImportSignals value, signed at extract time and
    // verified at import, persisted as a nullable owned value on Resume. Until it lands there is no
    // input, ApplicableSections is called with false, and the section is renormalized out of every
    // report.
    //
    // WHOEVER LANDS T3.5 MUST LAND BOTH HALVES IN ONE CHANGE. Flipping applicability on while this still
    // answers zero would give the section its 0.10 weight against a hard zero and cap every candidate at
    // 0.90 -- ten points off everyone, for a question the product still could not ask them. That is the
    // exact failure ScoringWeightsSnapshot.RenormalizedTo's remark on Languages describes, and it is
    // cheaper to state here than to rediscover.
    internal static double AtsParseabilityScore() => NotApplicableScore;
}

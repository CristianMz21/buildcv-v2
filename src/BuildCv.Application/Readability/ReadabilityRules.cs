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
//   Completeness    reads Educations, Skills and ContactInformation.Summary
//   Contact         reads ContactInformation.PhoneNumber, .Location, .Website and .Profiles
//   Achievements    reads Experience.Highlights
//   Chronology      reads Experience.Period and the number of experience entries
//   AtsParseability reads ImportSignals, and NOTHING ELSE READS IT -- the disjointness is free here,
//                   because it grades the uploaded document rather than anything the candidate typed.
//
// Static and pure: ReadabilityEngine is registered as a singleton and shared across every request.
internal static class ReadabilityRules
{
    // What a section scores when it cannot be measured at all. NOTHING WAS MEASURED, so the honest answer
    // is zero and the weight is zero beside it -- the section is renormalized out of the total by
    // ReadabilityWeightsSnapshot.RenormalizedTo and cannot move it in either direction.
    internal const double NotApplicableScore = 0.0;

    // The four sections computable from any resume at all. AtsParseability is absent because it needs a
    // document to grade, and ApplicableSections is the only place that can add it.
    private static readonly ReadabilitySectionType[] AlwaysApplicable =
    [
        ReadabilitySectionType.Completeness,
        ReadabilitySectionType.Contact,
        ReadabilitySectionType.Achievements,
        ReadabilitySectionType.Chronology,
    ];

    // Which sections could be measured for this resume. The weights are renormalized across exactly
    // these, so THE CEILING IS 1.00 EITHER WAY -- a clean single-column PDF with a text layer scores 100,
    // not 90, and a resume typed by hand is not charged for a document that does not exist.
    //
    // The parameter is a BOOL rather than a Resume so both directions stay trivially exercisable: its one
    // call site in ReadabilityEngine passes `resume.ImportSignals is not null`, and every renormalization
    // test can ask for the other answer without constructing an aggregate to get it.
    //
    // MERE PRESENCE IS ENOUGH, and that rests on AtsSignalCounts: whether the document yielded text is
    // known for every format, so a set of signals always has at least one measurable term and can never
    // be applicable-but-unmeasurable -- which would be the 0.90 cap this renormalization exists to
    // prevent, reintroduced one level down.
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

    // ANY DIGIT, EXCEPT A BARE CALENDAR YEAR. Still one sentence, so a candidate can check it against
    // their own bullet, which is the property that keeps the advice honest -- a rule nobody can verify
    // promises a gain it may then decline to pay.
    //
    // The exclusion earns its clause because THE FAILURE HAS A DIRECTION. Counting any digit credits
    // "Led the 2019 migration" as quantified, so the candidate is NOT told to add metrics -- and the
    // person who writes years instead of numbers is exactly the person that advice was written for.
    // Withholding it is the expensive mistake; offering advice someone can ignore is the cheap one.
    // Achievements carries 0.25 of the readability total, so this is a quarter of the score.
    //
    // Any standalone 1900-2100 token is excluded, REGARDLESS of what it means in the sentence. So
    // "Resolved 2019 incidents" is under-counted: the rule cannot tell a count from a date without
    // reading the words around it, and that is the clause creep this comment is otherwise avoiding.
    // Under-counting is the direction to fail in -- it offers advice the candidate can dismiss rather
    // than withholding advice they needed. "Migrated 3 services", "cut latency 40%" and "v2 of the
    // API" all still count, because none of those digits stand alone as a year.
    internal static bool StatesANumber(string highlight)
    {
        foreach (var token in highlight.Split(NumberTokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.Any(char.IsDigit))
                continue;

            if (!IsBareCalendarYear(token))
                return true;
        }

        return false;
    }

    private static readonly char[] NumberTokenSeparators =
        [' ', '\t', '\n', '\r', ',', ';', '(', ')', '[', ']', '"', '\''];

    // 1900-2100 written on its own, optionally closed by sentence punctuation: the shape a date takes
    // in a bullet point. A year glued to anything else ("2019-2021", "2019x") is not this.
    private static bool IsBareCalendarYear(string token)
    {
        var trimmed = token.TrimEnd('.', ':', '-', '–', '—');
        return trimmed.Length == 4
            && trimmed.All(char.IsAsciiDigit)
            && int.TryParse(trimmed, out var year)
            && year is >= 1900 and <= 2100;
    }

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
        // StartsOn and EndsOn throughout, never Start and End: those are PartialDates, which carry only
        // as much precision as their source stated and are deliberately not orderable or subtractable.
        // The two day-valued views ARE the convention on DateRange — a period runs from the first day its
        // start could mean to the last day its end could mean — so a gap measured through them is the
        // smallest one the document supports, which is the same direction every other judgement in these
        // rules takes. A full-precision period is unaffected: both views are the stated day.
        var ordered = resume.Experiences
            .OrderBy(experience => experience.Period.StartsOn)
            .ThenBy(experience => experience.Period.EndsOn ?? referenceDate)
            .ToList();

        List<EmploymentGap> gaps = [];
        if (ordered.Count == 0)
            return gaps;

        // An open-ended entry runs to the reference date, which is the same reading ScoringRules gives
        // DateRange.DurationInDays: "Present" means today, so a current role covers up to today.
        var coverageEnd = ordered[0].Period.EndsOn ?? referenceDate;

        for (var index = 1; index < ordered.Count; index++)
        {
            var entry = ordered[index];
            var gapDays = entry.Period.StartsOn.DayNumber - coverageEnd.DayNumber;
            if (gapDays > MaxGapDays)
                gaps.Add(new EmploymentGap(gapDays, entry));

            var entryEnd = entry.Period.EndsOn ?? referenceDate;
            if (entryEnd > coverageEnd)
                coverageEnd = entryEnd;
        }

        return gaps;
    }

    // ATS PARSEABILITY -- the share of the things an ATS needs from a document that the one this resume
    // was imported from actually provided: text it can extract without OCR, and a single reading column.
    //
    // A SHARE, exactly like Completeness and Contact, and for the same reason: a share needs no invented
    // constant to express severity. The two terms are not equally bad -- no text at all means an ATS
    // reads NOTHING, while two columns means it reads everything in the wrong order -- but that
    // difference belongs in the sentence the candidate is shown, not in a weighting nobody can check.
    //
    // THE DENOMINATOR MOVES, and that is the whole of what this rule has to say about Unknown. A
    // non-PDF upload carries no geometry, so "we could not tell" is not evidence of a problem and must
    // not be scored as one: the column term is dropped from BOTH halves of the fraction, and the
    // document is graded on what was actually measured. Penalising Unknown would charge a DOCX -- the
    // most ATS-parseable format there is -- for the detector's silence.
    //
    // IT IS ONLY EVER EVIDENCE ABOUT THE UPLOADED DOCUMENT, never about the CV as it now stands. A
    // candidate who imports a scanned PDF and then corrects every field by hand still scores what the
    // scan scored, because the file is deliberately never kept and there is nothing else to re-read. The
    // section's 0.10 -- the smallest of the five -- is sized for exactly that.
    internal static double AtsParseabilityScore(double metSignals, double measurableSignals) =>
        measurableSignals <= 0.0
            ? NotApplicableScore
            : Math.Clamp(metSignals / measurableSignals, 0.0, 1.0);

    // The numerator and the denominator the formula wants. Measurable is never zero for signals that
    // exist -- whether text came out is known for every format -- which is what makes
    // ApplicableSections safe to key on the mere presence of the value.
    internal static (int Met, int Measurable) AtsSignalCounts(ImportSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var met = ExtractsMachineReadableText(signals) ? 1 : 0;
        var measurable = 1;

        if (HasColumnEvidence(signals))
        {
            measurable++;
            if (ReadsInOneColumn(signals))
                met++;
        }

        return (met, measurable);
    }

    // ONE term from TWO facts, because they are two causes of one failure: a scanned PDF and an empty
    // document both hand an ATS nothing. They stay separate on ImportSignals because the candidate's fix
    // differs -- export it properly, or upload the right file -- and the two rules that say so are the
    // reason the distinction is worth carrying.
    internal static bool ExtractsMachineReadableText(ImportSignals signals) =>
        signals.HadTextLayer && !signals.Warnings.HasFlag(ImportWarningFlags.NoTextContent);

    internal static bool HasColumnEvidence(ImportSignals signals) =>
        signals.ColumnLayout != ColumnLayout.Unknown;

    internal static bool ReadsInOneColumn(ImportSignals signals) =>
        signals.ColumnLayout == ColumnLayout.Single;
}

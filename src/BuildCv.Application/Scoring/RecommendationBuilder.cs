namespace BuildCv.Application.Scoring;

using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

// Deterministic advice about the gap between one resume and one posting.
//
// A static class with per-section generator methods rather than an IRecommendationRule interface with
// DI registration: an interface would add a registration surface, an ordering question and a
// configuration nobody asked for, and would stop the whole rule set being readable from one file.
// Static also makes "stateless" unforgeable, which matters because ScoringEngine is a singleton.
//
// IMPACT IS DERIVED, NEVER ASSIGNED.
//
//     Impact = the increase in WeightedTotal this analysis would gain if this advice were fully acted on.
//
// Every rule computes it the same way: take the section score the breakdown already holds, evaluate
// the SAME ScoringRules formula the engine used with exactly one gap closed, and multiply the
// difference by that section's weight. Nothing here states a number a reader could not reproduce by
// re-scoring, which is the property ActingOnARecommendationTests measures rule by rule.
//
// A number that came from someone's intuition would be worse than no number at all: it gives a
// candidate false precision they cannot verify, about the one thing they are here to improve.
internal static class RecommendationBuilder
{
    // Ten is a reading limit, not a scoring one. Past it a candidate is being handed a backlog rather
    // than advice, and the total order guarantees the ten kept are the ten worth most.
    internal const int MaxRecommendations = 10;

    // The two thresholds Priority is a pure function of. One rule in one place, so the number and the
    // label can never disagree -- hand-assigned per-rule priorities are how a rule set drifts into
    // "everything is Critical".
    private const double CriticalImpact = 0.10;
    private const double ImportantImpact = 0.03;

    // Concatenated in SectionType order, then sorted. The concatenation order is not the output order
    // and never has to be, but a fixed one keeps the diff of "what does this emit" readable.
    internal static IReadOnlyList<Recommendation> Build(
        Resume resume, JobPosting jobPosting, ScoreBreakdown breakdown, DateOnly referenceDate)
    {
        List<Recommendation> found =
        [
            .. ForSkills(resume, jobPosting, breakdown),
            .. ForExperience(resume, breakdown, referenceDate),
            .. ForEducation(resume, breakdown),
            .. ForCertifications(resume, breakdown, referenceDate),
            .. ForProjects(resume, breakdown),
            .. ForLanguages(resume, jobPosting, breakdown),
        ];

        return [.. RecommendationOrder.Sort(found).Take(MaxRecommendations)];
    }

    // One recommendation per unmet requirement, never one lumped "add the missing skills": the impacts
    // differ, and a candidate deciding where to spend an afternoon needs them apart.
    //
    // Impact is what closing THIS requirement alone is worth. The section score is
    // matched/total, so satisfying one requirement of weight w moves it to (matched + w)/total --
    // evaluated through the engine's own SkillsScore rather than restated as w/total, so a change to
    // the formula cannot leave the advice quoting the old one.
    //
    // A posting whose requirements are all weighted zero produces impacts of zero, and the advice is
    // still emitted: the posting asked for the skill, so it is still worth adding. The number honestly
    // says it will not move the score, and the priority rule turns that into NiceToHave by itself.
    private static IEnumerable<Recommendation> ForSkills(
        Resume resume, JobPosting jobPosting, ScoreBreakdown breakdown)
    {
        var (matched, total) = ScoringRules.SkillWeights(resume, jobPosting);
        var current = breakdown.SkillsScore;

        foreach (var requirement in jobPosting.Requirements)
        {
            if (ScoringRules.IsSatisfiedBy(requirement, resume))
                continue;

            var impact = breakdown.Weights.Skills
                * (ScoringRules.SkillsScore(matched + requirement.Weight, total) - current);

            var mustHave = requirement.Priority == RequirementPriority.MustHave;

            yield return Advice(
                SectionType.Skills,
                mustHave ? RecommendationKind.MissingMustHaveSkill : RecommendationKind.MissingNiceToHaveSkill,
                mustHave
                    ? $"Add '{requirement.Skill.Name}' to your skills, project technologies or skill keywords: this posting lists it as a must-have."
                    : $"Add '{requirement.Skill.Name}' to your skills, project technologies or skill keywords: this posting lists it as nice to have.",
                impact,
                closesUnmatchedMustHave: mustHave);
        }
    }

    // THE ONLY EXPERIENCE RULE, and the omission is the point. "Have more years of experience" is a
    // fact about time, not advice: a candidate cannot act on it this week or ever, and attaching an
    // impact number to it would dress an unactionable statement up as a plan. Mis-tagged experience IS
    // actionable and its impact is exactly computable, so that is the one rule this section gets.
    //
    // One recommendation for the whole set rather than one per entry: the section score is capped at
    // five years, so per-entry impacts would not sum to the group's -- the last entry to be re-tagged
    // is often worth nothing. The group Δ is the honest number.
    //
    // Emitted only when the section is genuinely below its cap, which is the same "score < 1.0" test
    // the other counted sections use. Advice to re-label work for zero gain is not advice, and here it
    // would be advice to re-label VOLUNTEER work for zero gain.
    private static IEnumerable<Recommendation> ForExperience(
        Resume resume, ScoreBreakdown breakdown, DateOnly referenceDate)
    {
        var unmarked = resume.Experiences.Count(e => e.Type != ExperienceType.Professional);
        if (unmarked == 0)
            yield break;

        var professionalDays = ScoringRules.ProfessionalDays(resume, referenceDate);
        var unmarkedDays = ScoringRules.UnmarkedExperienceDays(resume, referenceDate);

        var current = breakdown.ExperienceScore;
        var impact = breakdown.Weights.Experience
            * (ScoringRules.ExperienceScore(professionalDays + unmarkedDays) - current);

        if (impact <= 0.0)
            yield break;

        var noun = unmarked == 1 ? "entry" : "entries";
        yield return Advice(
            SectionType.Experience,
            RecommendationKind.ExperienceNotMarkedProfessional,
            $"Check the type of {unmarked} experience {noun}: time recorded as anything other than Professional is not counted.",
            impact);
    }

    private static IEnumerable<Recommendation> ForEducation(Resume resume, ScoreBreakdown breakdown)
    {
        var current = breakdown.EducationScore;
        if (current >= ScoringRules.EducationWithDegreeScore)
            yield break;

        // Both rules close the same gap -- an education entry naming a degree takes the section to its
        // ceiling either way -- so both impacts are (ceiling - current) x the weight and only the advice
        // differs. Which one fires is decided by whether there is anything to add a degree TO.
        var impact = breakdown.Weights.Education * (ScoringRules.EducationWithDegreeScore - current);

        yield return resume.Educations.Count == 0
            ? Advice(
                SectionType.Education,
                RecommendationKind.NoEducationRecorded,
                "Add your education, including the degree: this resume records none, so the section scores zero.",
                impact)
            : Advice(
                SectionType.Education,
                RecommendationKind.NoDegreeRecorded,
                "Name the degree on at least one education entry: an entry without one does not count in full.",
                impact);
    }

    // One recommendation, not one per missing certification. The advice is "add another one", and its
    // impact is what ONE more is worth -- a third of the section, until the cap of three is reached.
    // Emitting three copies of the same sentence with the same impact would be three chances to act on
    // the same advice and only one of them would pay.
    private static IEnumerable<Recommendation> ForCertifications(
        Resume resume, ScoreBreakdown breakdown, DateOnly referenceDate)
    {
        var current = breakdown.CertificationsScore;
        if (current >= 1.0)
            yield break;

        var valid = ScoringRules.ValidCertificateCount(resume, referenceDate);
        var impact = breakdown.Weights.Certifications * (ScoringRules.CertificationsScore(valid + 1) - current);

        yield return Advice(
            SectionType.Certifications,
            RecommendationKind.FewerCertificationsThanExpected,
            $"Add a certification that is still valid: {valid} of {ScoringRules.CertificationCap:0} count towards this section today.",
            impact);
    }

    private static IEnumerable<Recommendation> ForProjects(Resume resume, ScoreBreakdown breakdown)
    {
        var current = breakdown.ProjectsScore;
        if (current >= 1.0)
            yield break;

        var qualifying = ScoringRules.QualifyingProjectCount(resume);
        var impact = breakdown.Weights.Projects * (ScoringRules.ProjectsScore(qualifying + 1) - current);

        yield return Advice(
            SectionType.Projects,
            RecommendationKind.FewerProjectsThanExpected,
            $"Add a project and list its technologies or highlights: {qualifying} of {ScoringRules.ProjectCap:0} count towards this section today. A project with neither is not counted.",
            impact);
    }

    // Three kinds for three genuinely different gaps, because the actions differ. LanguageLevelNotRecorded
    // is the one worth having: the candidate already speaks the language, and what is missing is a field.
    // Turning that into a silent penalty is what this whole design refuses to do.
    private static IEnumerable<Recommendation> ForLanguages(
        Resume resume, JobPosting jobPosting, ScoreBreakdown breakdown)
    {
        var requirementCount = jobPosting.LanguageRequirements.Count;
        if (requirementCount == 0)
            yield break;

        var satisfied = ScoringRules.SatisfiedLanguageCount(resume, jobPosting);
        var current = breakdown.LanguagesScore;
        var impact = breakdown.Weights.Languages
            * (ScoringRules.LanguagesScore(satisfied + 1, requirementCount) - current);

        foreach (var requirement in jobPosting.LanguageRequirements)
        {
            var gap = ScoringRules.EvaluateLanguage(requirement, resume);
            if (gap == LanguageGap.Satisfied)
                continue;

            var (kind, message) = gap switch
            {
                LanguageGap.Missing => (
                    RecommendationKind.LanguageMissing,
                    $"Add '{requirement.Name}' to your languages: this posting requires {requirement.MinimumLevel} or above."),
                LanguageGap.BelowRequiredLevel => (
                    RecommendationKind.LanguageBelowRequiredLevel,
                    $"Raise your recorded level for '{requirement.Name}': this posting requires {requirement.MinimumLevel} or above."),
                _ => (
                    RecommendationKind.LanguageLevelNotRecorded,
                    $"Record a level for '{requirement.Name}': this posting requires {requirement.MinimumLevel} or above, and the fluency text beside it is never read as a level."),
            };

            yield return Advice(SectionType.Languages, kind, message, impact);
        }
    }

    private static Recommendation Advice(
        SectionType section,
        RecommendationKind kind,
        string message,
        double impact,
        bool closesUnmatchedMustHave = false) =>
        Recommendation.Create(
            section,
            PriorityFor(impact, closesUnmatchedMustHave),
            kind,
            message,
            // Derived impacts are non-negative by construction -- every target above is the same
            // monotone formula evaluated with one more unit of the thing it counts -- and no weight
            // exceeds 1.0, so the product stays inside the unit interval Recommendation.Create demands.
            // The clamp is here so a future rule that got the direction wrong fails its own impact test
            // rather than throwing InvalidRecommendationException from inside a scoring request.
            Math.Clamp(impact, 0.0, 1.0));

    // Priority is a pure function of Impact plus the must-have gate, and nothing else may set it.
    //
    // The gate exists because an unmatched must-have is not merely worth points: it is the requirement
    // the posting screens on, so a candidate needs it first even when a bigger number sits below it.
    // Everything else is decided by what acting on it is worth, so the label can never drift from the
    // number beside it.
    private static RecommendationPriority PriorityFor(double impact, bool closesUnmatchedMustHave) =>
        closesUnmatchedMustHave || impact >= CriticalImpact ? RecommendationPriority.Critical
        : impact >= ImportantImpact ? RecommendationPriority.Important
        : RecommendationPriority.NiceToHave;
}

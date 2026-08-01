namespace BuildCv.Application.Scoring;

using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;

// Every section formula and every predicate it is built from, in ONE place, because two consumers
// read them and a disagreement between those two consumers is invisible.
//
// ScoringEngine turns them into the six section scores. RecommendationBuilder evaluates the SAME
// formula a second time with one gap closed, and reports the difference as Recommendation.Impact --
// so the number a candidate is shown is "the score you would get", not an estimate of it. Duplicating
// "does this resume satisfy this requirement" anywhere would let the advice promise a gain the engine
// does not pay, and nothing would fail.
//
// Static and pure: ScoringEngine is registered as a singleton and shared across every request.
internal static class ScoringRules
{
    // What a section is worth when the posting states nothing about it. It must neither reward nor
    // punish, and half the section's weight is the only answer with that property.
    internal const double NeutralScore = 0.5;

    // The counts a resume is scored against. They are caps, not targets to exceed: a fourth
    // certification is worth nothing, which is exactly why a recommendation to add one is only
    // emitted below the cap.
    internal const double CertificationCap = 3.0;
    internal const double ProjectCap = 3.0;
    internal const double ExperienceDaysCap = 365.0 * 5;

    // The two rungs of the education ladder. The gap between them is the whole of what NoDegreeRecorded
    // is worth, and the ceiling is what both education rules aim at -- named rather than written as a
    // literal 1.0 in two files, so the advice cannot end up quoting a target the formula no longer has.
    internal const double EducationWithoutDegreeScore = 0.7;
    internal const double EducationWithDegreeScore = 1.0;

    // Σ(weight of matched) / Σ(weight of all). The magnitude now comes from JobRequirement.Weight,
    // which defaults from Priority, so a posting that states nothing scores exactly as it did when
    // the engine derived the number from Priority inline.
    //
    // Total weight of zero covers two different postings -- one with no requirements at all, and one
    // whose requirements are all weighted 0.0 -- and both mean the same thing: this posting expresses
    // no opinion about skills. Without the guard the second one divides by zero.
    internal static double SkillsScore(double matchedWeight, double totalWeight) =>
        totalWeight <= 0.0 ? NeutralScore : Math.Clamp(matchedWeight / totalWeight, 0.0, 1.0);

    internal static (double Matched, double Total) SkillWeights(Resume resume, JobPosting jobPosting)
    {
        double matched = 0;
        double total = 0;

        foreach (var requirement in jobPosting.Requirements)
        {
            total += requirement.Weight;
            if (IsSatisfiedBy(requirement, resume))
                matched += requirement.Weight;
        }

        return (matched, total);
    }

    internal static bool IsSatisfiedBy(JobRequirement requirement, Resume resume) =>
        resume.Skills.Any(s => s.Name.Name.Equals(requirement.Skill.Name, StringComparison.OrdinalIgnoreCase))
        || resume.Skills.Any(s => s.Keywords.Any(k => k.Equals(requirement.Skill.Name, StringComparison.OrdinalIgnoreCase)))
        || resume.Projects.Any(p => p.Technologies.Any(t => t.Name.Equals(requirement.Skill.Name, StringComparison.OrdinalIgnoreCase)));

    internal static double ExperienceScore(double days) => Math.Clamp(days / ExperienceDaysCap, 0.0, 1.0);

    internal static int ProfessionalDays(Resume resume, DateOnly referenceDate) =>
        resume.Experiences
            .Where(e => e.Type == ExperienceType.Professional)
            .Sum(e => e.Period.DurationInDays(referenceDate));

    // The mirror image of ProfessionalDays, and deliberately `!= Professional` rather than
    // `== Volunteer`: the API parses ExperienceType with Enum.TryParse and no Enum.IsDefined guard
    // (ResumeEndpoints), so a request can persist a value that is neither member. Such an entry fails
    // the `== Professional` test above and is excluded from the score, which harms only the candidate
    // who sent it -- and it lands here, where it becomes advice naming the fix instead of a silent
    // deduction. Widening this to `== Volunteer` would drop those entries out of both halves.
    internal static int UnmarkedExperienceDays(Resume resume, DateOnly referenceDate) =>
        resume.Experiences
            .Where(e => e.Type != ExperienceType.Professional)
            .Sum(e => e.Period.DurationInDays(referenceDate));

    internal static double EducationScore(Resume resume)
    {
        if (resume.Educations.Count == 0)
            return 0.0;

        return resume.Educations.Any(e => !string.IsNullOrWhiteSpace(e.Degree))
            ? EducationWithDegreeScore
            : EducationWithoutDegreeScore;
    }

    internal static double CertificationsScore(double validCount) => Math.Clamp(validCount / CertificationCap, 0.0, 1.0);

    internal static int ValidCertificateCount(Resume resume, DateOnly referenceDate) =>
        resume.Certificates.Count(c =>
            c.ValidityPeriod is null
            || c.ValidityPeriod.IsCurrent
            || c.ValidityPeriod.End >= referenceDate);

    internal static double ProjectsScore(double qualifyingCount) => Math.Clamp(qualifyingCount / ProjectCap, 0.0, 1.0);

    internal static int QualifyingProjectCount(Resume resume) =>
        resume.Projects.Count(p => p.Technologies.Count > 0 || p.Highlights.Count > 0);

    // Satisfied share, one vote per stated language. A posting that asks for no language gets the same
    // neutral 0.5 the skills section gives an empty requirement list -- it must neither reward the
    // monolingual candidate nor punish them for a question nobody asked.
    internal static double LanguagesScore(double satisfiedCount, double requirementCount) =>
        requirementCount <= 0.0 ? NeutralScore : Math.Clamp(satisfiedCount / requirementCount, 0.0, 1.0);

    internal static int SatisfiedLanguageCount(Resume resume, JobPosting jobPosting) =>
        jobPosting.LanguageRequirements.Count(r => EvaluateLanguage(r, resume) == LanguageGap.Satisfied);

    // Why a missing Level is its own answer rather than a low one: Language.Fluency is free text and
    // must never be parsed into a level (see the comment on Domain.Resumes.Language.Level -- an
    // unrecognised word would read as "not proficient" and score a native speaker at zero). So a
    // candidate holding the language with no Level recorded does NOT satisfy the requirement, and the
    // missing data is turned into a recommendation naming exactly what to add.
    //
    // Resume.AddLanguage rejects a duplicate name with OrdinalIgnoreCase, so at most one entry can
    // match and FirstOrDefault is unambiguous rather than order-dependent.
    internal static LanguageGap EvaluateLanguage(LanguageRequirement requirement, Resume resume)
    {
        var held = resume.Languages.FirstOrDefault(
            l => l.Name.Equals(requirement.Name, StringComparison.OrdinalIgnoreCase));

        if (held is null)
            return LanguageGap.Missing;
        if (held.Level is null)
            return LanguageGap.LevelNotRecorded;

        // `held >= required` is the whole comparison, which is only true because LanguageProficiency's
        // members ascend. That ordering is the contract stated on the enum itself.
        return held.Level >= requirement.MinimumLevel ? LanguageGap.Satisfied : LanguageGap.BelowRequiredLevel;
    }
}

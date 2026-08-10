namespace BuildCv.Application.Scoring;

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

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
    // What a section scores when the posting asks nothing of it. NOTHING WAS MEASURED, so the honest
    // answer is zero and the weight is zero beside it — the section is renormalized out of the total by
    // ScoringWeightsSnapshot.RenormalizedTo and cannot move it in either direction.
    //
    // This replaces a neutral 0.5. That number was a fabrication whose only justification was "neither
    // reward nor punish inside the total", and once the section stops contributing to the total it has
    // no justification left. It also cost every candidate half of the unasked section's weight, because
    // "neutral" was relative to the section's midpoint and never to its ceiling.
    //
    // A caller reading this score without its weight will misread it. That is exactly what SectionScore
    // exists to prevent: the two travel together and a weight of 0.0 says "not asked".
    internal const double NotApplicableScore = 0.0;

    // The four sections scored entirely from the candidate's own data, which is why they apply to every
    // posting. Verified rather than assumed: SkillWeights and SatisfiedLanguageCount are the ONLY
    // members here that take a JobPosting at all. JobPosting.EducationLevel exists but nothing in this
    // layer reads it, so Education still scores on what the candidate recorded.
    private static readonly SectionType[] AlwaysApplicable =
    [
        SectionType.Experience, SectionType.Education, SectionType.Certifications, SectionType.Projects
    ];

    // Which sections this posting actually asks about. The weights are renormalized across exactly
    // these, so a posting that states no skill and no language requirement is scored out of Experience,
    // Education, Certifications and Projects alone — and a candidate perfect in those four scores 1.00.
    internal static IReadOnlyList<SectionType> ApplicableSections(double skillWeightTotal, int languageRequirementCount)
    {
        List<SectionType> applicable = [.. AlwaysApplicable];

        // Total weight of zero covers two different postings — one with no requirements at all, and one
        // whose requirements are all weighted 0.0 — and both mean the same thing: this posting expresses
        // no scoreable opinion about skills. It is also the guard that stops the share being 0/0.
        if (skillWeightTotal > 0.0)
            applicable.Add(SectionType.Skills);
        if (languageRequirementCount > 0)
            applicable.Add(SectionType.Languages);

        return applicable;
    }

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

    // Σ(weight of matched) / Σ(weight of all). The magnitude comes from JobRequirement.Weight, which
    // defaults from Priority, so a posting that states no explicit weight scores exactly as it did when
    // the engine derived the number from Priority inline.
    //
    // Total weight of zero means the section does not apply -- ApplicableSections uses the same test --
    // so it scores NotApplicableScore and is renormalized out of the total rather than divided by zero.
    internal static double SkillsScore(double matchedWeight, double totalWeight) =>
        totalWeight <= 0.0 ? NotApplicableScore : Math.Clamp(matchedWeight / totalWeight, 0.0, 1.0);

    internal static (double Matched, double Total) SkillWeights(
        Resume resume, JobPosting jobPosting, ISkillLexicon skillLexicon)
    {
        double matched = 0;
        double total = 0;

        foreach (var requirement in jobPosting.Requirements)
        {
            // TOTAL IS COMPUTED WITHOUT THE LEXICON, which is what makes the weights identical under any
            // lexicon: ApplicableSections is asked about this total, so the six renormalized weights --
            // and therefore every other section's contribution to the score -- cannot move because a
            // skill started matching.
            total += requirement.Weight;
            if (IsSatisfiedBy(requirement, resume, skillLexicon))
                matched += requirement.Weight;
        }

        return (matched, total);
    }

    // THE THREE PLACES A REQUIREMENT IS COMPARED AGAINST A RESUME: the skill's name, the keywords beside
    // it, and the technologies on a project. Each is a whole-string test first and a lexicon lookup only
    // if that fails.
    //
    // ADDITIVE BY CONSTRUCTION. NamesTheSameSkill answers true whenever the old expression did, because
    // the old expression IS its first operand, unchanged. So a lexicon entry can turn a miss into a match
    // and can never turn a match into a miss -- no candidate's score can go down, whatever the file says.
    // With an empty lexicon Canonicalize is the identity, so the second operand becomes a re-run of the
    // first and the whole rule collapses to what it was. Both halves are executed by
    // EmptyLexiconEquivalenceTests.
    //
    // WHAT THE ORDERING BUYS, STATED EXACTLY, because the obvious phrasing overclaims. Deleting the exact
    // comparison and keeping only the canonical one reds nothing against any lexicon that HONOURS the
    // port contract -- measured with a negative control, not assumed. It cannot: rules 2 and 3 there
    // (unrecognised terms come back unchanged, recognition ignores case) already make the canonical
    // comparison true wherever whole-string equality is. So the contract, not this line, is what makes a
    // conforming lexicon additive. This line is what makes additivity independent OF that contract: every
    // match the previous engine made survives any implementation, correct or not. The single test that
    // can see the difference is SkillLexiconMatchingTests.
    // Match_WhenTheLexiconDisagreesWithItselfAboutCase_TheExactComparisonStillWins, which supplies a
    // deliberately non-conforming one.
    //
    // WHY THIS IS WORTH DOING AT ALL: whole-string equality told a candidate who had listed "React.js" to
    // ADD "React", at Critical priority and with an exact Impact beside it. Authoritative-looking, and
    // wrong in the direction that costs the candidate -- the same failure shape Language.Fluency was
    // sealed to prevent, where an unrecognised value would have read as "unmet".
    //
    // THE COST, stated because it is real: canonicalization MERGES, so a careless entry in the lexicon
    // makes this report a requirement satisfied that is not. That risk lives entirely in the data, which
    // is why the data is a reviewed file with a collision suite rather than a table anyone can edit.
    internal static bool IsSatisfiedBy(JobRequirement requirement, Resume resume, ISkillLexicon skillLexicon) =>
        resume.Skills.Any(s => NamesTheSameSkill(s.Name.Name, requirement.Skill.Name, skillLexicon))
        || resume.Skills.Any(s => s.Keywords.Any(k => NamesTheSameSkill(k, requirement.Skill.Name, skillLexicon)))
        || resume.Projects.Any(p => p.Technologies.Any(t => NamesTheSameSkill(t.Name, requirement.Skill.Name, skillLexicon)));

    // OrdinalIgnoreCase on BOTH comparisons, deliberately the same comparer. The canonical tokens are
    // curated, so nothing is folded here that the file did not fold -- and using a STRICTER comparer for
    // the second test would let a lexicon entry be narrower than the exact match it is meant to widen,
    // which is a way for the additive property to become a claim about the data instead of about the code.
    private static bool NamesTheSameSkill(string candidate, string required, ISkillLexicon skillLexicon) =>
        candidate.Equals(required, StringComparison.OrdinalIgnoreCase)
        || skillLexicon.Canonicalize(candidate).Equals(
            skillLexicon.Canonicalize(required), StringComparison.OrdinalIgnoreCase);

    // The same three comparisons IsSatisfiedBy makes, reported instead of reduced to a bool.
    //
    // IT SHARES NamesTheSameSkill RATHER THAN RESTATING IT, which is the only property that matters here:
    // a second copy of the comparison would be a second statement of the matching rule, and the two would
    // drift the first time the lexicon logic moved -- publishing an attribution that disagrees with the
    // score it was published beside. So Satisfied below is not "what the attribution found"; it is
    // MatchedBy being non-empty, and MatchedBy is built by the comparer that scored.
    //
    // Reads nothing and writes nothing: no score, no weight, no total. Deleting this method leaves every
    // number in this file identical, which is what makes it safe to add to a shipped engine.
    internal static IReadOnlyList<RequirementAttribution> Attribute(
        Resume resume, JobPosting jobPosting, ISkillLexicon skillLexicon)
    {
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(jobPosting);
        ArgumentNullException.ThrowIfNull(skillLexicon);

        var attributions = new List<RequirementAttribution>(jobPosting.Requirements.Count);

        // ONE ENTRY PER REQUIREMENT, satisfied or not. An unsatisfied requirement carries an empty
        // MatchedBy, which is what lets a client stop inferring absence from the text of a recommendation:
        // advice is capped at ten, so "no recommendation mentions React" never meant "React matched".
        foreach (var requirement in jobPosting.Requirements)
        {
            var evidence = new List<RequirementEvidence>();

            foreach (var skill in resume.Skills)
            {
                if (NamesTheSameSkill(skill.Name.Name, requirement.Skill.Name, skillLexicon))
                    evidence.Add(new RequirementEvidence(RequirementMatchSource.SkillName, skill.Name.Name));

                foreach (var keyword in skill.Keywords)
                {
                    if (NamesTheSameSkill(keyword, requirement.Skill.Name, skillLexicon))
                        evidence.Add(new RequirementEvidence(RequirementMatchSource.SkillKeyword, keyword));
                }
            }

            foreach (var technology in resume.Projects.SelectMany(project => project.Technologies))
            {
                if (NamesTheSameSkill(technology.Name, requirement.Skill.Name, skillLexicon))
                    evidence.Add(new RequirementEvidence(RequirementMatchSource.ProjectTechnology, technology.Name));
            }

            attributions.Add(new RequirementAttribution(
                requirement.Skill.Name,
                requirement.Priority,
                requirement.Weight,
                evidence.Count > 0,
                evidence));
        }

        return attributions;
    }

    internal static double ExperienceScore(double days) => Math.Clamp(days / ExperienceDaysCap, 0.0, 1.0);

    internal static int ProfessionalDays(Resume resume, DateOnly referenceDate) =>
        resume.Experiences
            .Where(e => e.Type == ExperienceType.Professional)
            .Sum(e => e.Period.DurationInDays(referenceDate));

    // The mirror image of ProfessionalDays, and deliberately `!= Professional` rather than
    // `== Volunteer`, so the two halves are exhaustive by construction.
    //
    // Both write paths for ExperienceType now reject undefined values before they reach a column —
    // ResumeEndpoints guards its Enum.TryParse with Enum.IsDefined, and the draft import goes through
    // FieldErrorCollector.ParseOptionalEnum, which does the same. That was NOT true when this rule was
    // written: the endpoint accepted any numeric string and the tinyint conversion is unchecked, so a
    // row could hold a value that is neither member. Rows written before that guard landed can still
    // hold one, and `!= Professional` is what keeps them counted as unmarked experience — advice
    // naming the fix — instead of vanishing from both halves the way `== Volunteer` would make them.
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

    // EndsOn, not End: an expiry the candidate stated only as a month expires on the LAST day of it,
    // which is DateRange's convention everywhere and the reading that does not take a certificate away
    // from someone whose card says "valid to 06/2027". A full date is unaffected — EndsOn is that day.
    internal static int ValidCertificateCount(Resume resume, DateOnly referenceDate) =>
        resume.Certificates.Count(c =>
            c.ValidityPeriod is null
            || c.ValidityPeriod.IsCurrent
            || c.ValidityPeriod.EndsOn >= referenceDate);

    internal static double ProjectsScore(double qualifyingCount) => Math.Clamp(qualifyingCount / ProjectCap, 0.0, 1.0);

    internal static int QualifyingProjectCount(Resume resume) =>
        resume.Projects.Count(p => p.Technologies.Count > 0 || p.Highlights.Count > 0);

    // Satisfied share, one vote per stated language. A posting that asks for no language does not apply,
    // exactly as an empty skill requirement list does not: the section scores nothing, carries no weight
    // and cannot punish a candidate for a question nobody put to them.
    internal static double LanguagesScore(double satisfiedCount, double requirementCount) =>
        requirementCount <= 0.0 ? NotApplicableScore : Math.Clamp(satisfiedCount / requirementCount, 0.0, 1.0);

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

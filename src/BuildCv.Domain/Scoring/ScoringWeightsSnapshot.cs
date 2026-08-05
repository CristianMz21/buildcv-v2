namespace BuildCv.Domain.Scoring;

public sealed record ScoringWeightsSnapshot
{
    // THE SCORING MODEL VERSION: which WEIGHTS **AND WHICH FORMULAS** produced a set of numbers — not
    // which shape they serialize in.
    //
    // IT IS 3 BECAUSE THE MATCHING RULE MOVED AND NO WEIGHT DID. ScoringRules.IsSatisfiedBy now consults
    // a skill lexicon after whole-string equality fails, so a resume listing "React.js" satisfies a
    // requirement for "React" that it did not satisfy under v2. Default() is untouched: a v2 and a v3
    // analysis of the same pair can carry byte-identical weights and different totals, which is precisely
    // the case a version that only ever meant "the weighting" could not describe.
    //
    // WHICH DIRECTION IT MOVES, because "not comparable" reads as "could be worse" and here it cannot be.
    // The lexicon is consulted only AFTER the exact comparison fails, so the v2 expression is the first
    // operand of the v3 one, unchanged: a lexicon entry turns a miss into a match and never the reverse.
    // NO RESUME SCORES LOWER UNDER v3 THAN IT DID UNDER v2. Additive by construction, and executed over a
    // vocabulary of aliases and near-collisions by EmptyLexiconEquivalenceTests.
    //
    // "WEIGHTING" WAS THE ORIGINAL WORDING AND IT WAS TOO NARROW. The formulas live in ScoringRules, in
    // another project, and nothing couples them to this number: commit 3508f35 replaced an unasked
    // section's score of 0.5 with 0.0 — a change its own comment describes as moving real candidates'
    // totals — and left this constant at the 2 an earlier commit had already set. So analyses on both
    // sides of that change are stamped v2 and were not scored by the same model, which is exactly the
    // comparison this number exists to keep honest.
    //
    // THIS BUMP STOPS THAT DRIFT; IT DOES NOT REPAIR IT. Every row already written is stamped v2 and stays
    // stamped v2, so the pre-3508f35 and post-3508f35 rows remain indistinguishable from each other
    // forever — nothing can tell them apart after the fact. What changes is that v3 means one model, and
    // that the next engine change has a worked example to follow rather than a precedent to repeat.
    //
    // WHAT A DEPLOYMENT SEES, and it should be expected rather than debugged. ScoreResume's de-duplication
    // key compares the stored SchemaVersion against this constant, so on the release that lands this every
    // stored analysis stops being reusable and the next score of each pair writes a new row. One extra
    // history entry per pair per candidate, once. Their scores go up or stay level; none goes down.
    //
    // AND A v2 ROW WILL REPORT isStale FALSE WHILE STILL BEING RE-SCORED, which looks contradictory and is
    // not: Analysis.IsStaleFor answers "does this still describe the candidate's CV", and it reads the
    // provenance timestamps alone. This constant answers a different question — "was this produced by the
    // model running now". A row can honestly be current about the resume and stale about the model.
    //
    // THE BUMP RULE, stated for whoever changes the engine next: bump this when anything changes what a
    // given (resume, posting) would score. That is the weights in Default(); every constant and predicate
    // in ScoringRules — the caps, the two education rungs, NotApplicableScore, IsSatisfiedBy; and any
    // DATA those rules consult, so a skill-lexicon revision is a model change even though no weight moved.
    // It is not bumped for renaming, reordering or reserializing anything.
    //
    // The reference date is the one input that changes a score WITHOUT being a model change: it is data
    // about when the score was taken, and it is carried per row by Analysis.ScoredAt. Two analyses of one
    // resume taken on different days are the same model applied to a different day, not two models.
    //
    // ONE NUMBER, DELIBERATELY, and a second one must not be added beside it. Separate "weights version"
    // and "formula version" integers would require a comparability matrix — which pairs of which are
    // comparable — and every reader of a score would have to carry it. A single number that only ever
    // means "same model or not" needs no matrix.
    //
    // THE ONE-WAY DOOR, stated precisely, because this sentence ends up in a deployment plan and both
    // of its obvious phrasings are wrong. NOTE THAT v3 DOES NOT MOVE IT: this bump changes a number and
    // no member, so a v3 payload has the shape a v2 payload has and every reader that could load one can
    // load the other. The door below is about the Languages MEMBER, and it is where it always was.
    //
    // WHICH READER BREAKS: one built before the Languages MEMBER existed — i.e. before PR 1, not merely
    // before v2. A PR 2 build already has the six-member type, deserializes a v2 payload without
    // complaint, and merely reports weights that differ from its own Default(). So the boundary is
    // "no rolling back past PR 1", which is operationally the same thing only while this chain merges
    // as one release, and misleading the moment it ships in pieces.
    //
    // WHICH ROWS IT STRANDS: not all of them. A pre-PR-1 reader skips the unmapped member
    // (System.Text.Json defaults JsonUnmappedMemberHandling to Skip) and re-runs the sum invariant over
    // the five it can see, so renormalization decides the outcome:
    //
    //   - Posting stated a language requirement -> Languages carries weight -> the five it can see sum
    //     to less than 1.00, Create throws, and the row is UNREADABLE to that build.
    //   - Posting stated none -> RenormalizedTo drops Languages to 0.0, the other five already sum to
    //     1.00, and the row loads and reproduces the same total, because a zero-weighted section
    //     contributed nothing to it.
    //
    // Both directions are executed by
    // ValueObjectConverterTests.ScoringWeights_AVersionTwoPayloadIsUnreadableToAnOldReaderOnlyWhenLanguagesCarriesWeight
    // rather than reasoned about from the deserializer's documented default.
    //
    // AND THE UNREADABLE BRANCH IS UNREACHABLE ON REAL DATA TODAY, so do not plan a deployment around
    // it. Languages carries weight only when the posting states a LANGUAGE requirement, and nothing in
    // the shipped API can put one there: JobPosting.AddLanguageRequirement and SetEducationLevel still
    // have no caller in src/. (POST /job-offers/import DOES now call AddRequirement, so a candidate's
    // Draft offer can state SKILL requirements -- but skills drive the Skills weight, never the
    // Languages weight, so that path does not reach this branch.) Every row any deployment can actually
    // write takes the second branch and loads into a pre-PR-1 build unchanged. The test above reaches
    // the first branch by constructing the payload directly, which is the only way anything reaches it.
    //
    // THE ROLLBACK HAZARD THAT IS REACHABLE is not here at all — it is the migration.
    // Persistence/Migrations/20260801140223_AddSectionScoringAndRecommendations.Down() drops the
    // scoring.Recommendations table and Analyses.LanguagesScore, which is irrecoverable data loss on
    // every row rather than a parse failure a reader can be rebuilt around. That migration is
    // forward-only in practice; the reasoning is stated on its Down().
    public const int CurrentSchemaVersion = 3;

    public double Skills { get; }
    public double Experience { get; }
    public double Education { get; }
    public double Certifications { get; }
    public double Projects { get; }
    public double Languages { get; }
    public int SchemaVersion { get; }

    private ScoringWeightsSnapshot(
        double skills,
        double experience,
        double education,
        double certifications,
        double projects,
        double languages,
        int schemaVersion)
    {
        Skills = skills;
        Experience = experience;
        Education = education;
        Certifications = certifications;
        Projects = projects;
        Languages = languages;
        SchemaVersion = schemaVersion;
    }

    public static ScoringWeightsSnapshot Create(
        double skills,
        double experience,
        double education,
        double certifications,
        double projects,
        double languages,
        int schemaVersion = CurrentSchemaVersion)
    {
        // FINITE first, and this ordering matters. Every comparison below is false for NaN, so a NaN
        // weight passes the non-negative check AND passes the sum check (Math.Abs(NaN - 1.0) > 0.0001
        // is false), and would be persisted as a snapshot whose arithmetic can never be reproduced.
        // Reachable since RenormalizedTo divides.
        if (!double.IsFinite(skills) || !double.IsFinite(experience) || !double.IsFinite(education)
            || !double.IsFinite(certifications) || !double.IsFinite(projects) || !double.IsFinite(languages))
            throw new ArgumentException("Weights must be finite numbers.");

        if (skills < 0 || experience < 0 || education < 0 || certifications < 0 || projects < 0 || languages < 0)
            throw new ArgumentException("Weights must be non-negative.");

        // The invariant everything downstream leans on. It is what makes WeightedTotal a 0..1 number,
        // which is what makes Analysis.OverallScore a percentage, which is what makes ScoreBand's
        // thresholds mean anything. A five-member v1 payload still satisfies it: Languages reads back
        // as 0.0 and the other five already summed to 1.0.
        var sum = skills + experience + education + certifications + projects + languages;
        if (Math.Abs(sum - 1.0) > 0.0001)
            throw new ArgumentException($"Weights must sum to 1.0 (actual: {sum}).");

        if (schemaVersion < 1)
            throw new ArgumentException("SchemaVersion must be >= 1.", nameof(schemaVersion));

        return new ScoringWeightsSnapshot(
            skills, experience, education, certifications, projects, languages, schemaVersion);
    }

    // The redistribution: Education 0.20 → 0.10, Languages 0.00 → 0.10.
    //
    // WEIGHT AND SCORE ARRIVE TOGETHER, which is the only reason this is safe to do in one step. The
    // previous release shipped Languages shaped but unweighted precisely so that no window existed in
    // which a section carried weight that nothing computed — a 0.10 weight against a hard-coded 0.0
    // would have capped every candidate at 0.90 and taken up to ten points off everyone with an
    // education. This factory changes only alongside a Languages score the engine really produces.
    //
    // Scores DO move here, and that is the point rather than a regression: a candidate who speaks the
    // language a posting asks for gains up to ten points, and a candidate whose education was carrying
    // a fifth of their score now carries a tenth of it. SchemaVersion 2 is what keeps an old analysis
    // explainable under the model that produced it.
    public static ScoringWeightsSnapshot Default() => Create(0.45, 0.20, 0.10, 0.10, 0.05, 0.10);

    // A SECTION THAT DOES NOT APPLY DOES NOT CONSUME WEIGHT.
    //
    // The weight of every section the posting asks nothing of is redistributed PROPORTIONALLY across
    // the sections it does ask about, so the ceiling is 1.00 for every posting and the score means
    // "how well you match what you were actually asked" rather than "how well you match a fixed
    // six-part template".
    //
    // This replaces a neutral 0.5 that used to be handed to an unasked section. That number was
    // defensible as "neither reward nor punish" only relative to the MIDPOINT of the section, never
    // relative to its ceiling: half of an unasked section's weight was simply unreachable. A posting
    // stating no language requirement is the common case, not the rare one, so a flawless CV scored 95
    // and the candidate had no way to find out why — in a product whose whole purpose is explaining
    // their score to them.
    //
    // Renormalizing rather than special-casing Languages fixes both instances at once: the skills
    // section has had the identical defect since long before Languages existed.
    //
    // Identity when every section applies: the divisor is 1.0, so each weight is returned bit-for-bit
    // and a fully-specified posting is scored under exactly Default().
    //
    // SchemaVersion is CARRIED, NOT BUMPED. It names which scoring MODEL produced the numbers — the
    // weights and the formulas together; the snapshot itself names the RESULT of applying that model to
    // one posting. Every v2 analysis is explained by "the v2 model, renormalized to what this posting
    // asked", and the row stores the actual divisor's output, so the arithmetic is
    // reproducible from the row alone. Bumping per posting would make the version vary within one
    // model, which is the one thing it exists not to do.
    //
    // The consequence that follows from that: A PERSISTED v2 SNAPSHOT IS NO LONGER NECESSARILY
    // Default(). Anything comparing the two to decide "was this scored under the current model" must
    // read SchemaVersion instead.
    public ScoringWeightsSnapshot RenormalizedTo(IEnumerable<SectionType> applicableSections)
    {
        ArgumentNullException.ThrowIfNull(applicableSections);

        var applicable = new HashSet<SectionType>(applicableSections);

        // The degenerate case: nothing applies, or everything that does carries zero weight. It is
        // unreachable today — Experience, Education, Certifications and Projects are scored from the
        // candidate's own data and always apply, and they carry 0.45 between them — but there is no
        // renormalized set to return, and silently falling back to the unrenormalized weights would
        // reintroduce the unreachable ceiling this method exists to remove.
        var applicableTotal = applicable.Sum(WeightFor);
        if (applicableTotal <= 0.0)
            throw new ArgumentException(
                "At least one applicable section must carry weight.", nameof(applicableSections));

        double ShareFor(SectionType section) =>
            applicable.Contains(section) ? WeightFor(section) / applicableTotal : 0.0;

        // Create re-checks the sum, which is what makes this arithmetically sound rather than merely
        // plausible: the shares are w/T summed over exactly the sections that contributed T, so they
        // total 1.0 by construction, and the invariant catches it here if that ever stops being true.
        return Create(
            ShareFor(SectionType.Skills),
            ShareFor(SectionType.Experience),
            ShareFor(SectionType.Education),
            ShareFor(SectionType.Certifications),
            ShareFor(SectionType.Projects),
            ShareFor(SectionType.Languages),
            SchemaVersion);
    }

    public double WeightFor(SectionType section) => section switch
    {
        SectionType.Skills => Skills,
        SectionType.Experience => Experience,
        SectionType.Education => Education,
        SectionType.Certifications => Certifications,
        SectionType.Projects => Projects,
        SectionType.Languages => Languages,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown scoring section.")
    };
}

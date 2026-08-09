namespace BuildCv.Domain.Readability;

// Which weighting explained one readability report, carried on the row so an old report stays
// explainable after the rules move. Structurally a sibling of Scoring.ScoringWeightsSnapshot and
// deliberately not a reuse of it: that type has six members named after MATCH sections, and a weighting
// whose members are Skills and Languages cannot describe a score about a resume on its own.
public sealed record ReadabilityWeightsSnapshot
{
    // THE READABILITY MODEL VERSION: which WEIGHTS AND WHICH FORMULAS produced a set of numbers -- not
    // which shape they serialize in. It is 1 because this is the first release of the readability engine.
    //
    // IT IS ITS OWN NUMBER, and sharing Scoring.ScoringWeightsSnapshot.CurrentSchemaVersion would be the
    // mistake it exists to prevent: the two engines change independently, so one integer would either
    // invalidate every stored analysis whenever a readability rule moved, or claim two readability
    // reports were comparable when a scoring change had bumped it between them.
    //
    // THE BUMP RULE, stated for whoever changes the engine next: bump this when anything changes what a
    // given resume would score. That is the weights in Default(); every constant and predicate in
    // ReadabilityRules -- the section formulas, MaxGapDays, NotApplicableScore; and any DATA those rules
    // consult, so a revision of the action-verb list is a model change even though no weight moved. It is
    // not bumped for renaming, reordering or reserializing anything.
    //
    // The reference date is the one input that changes a report WITHOUT being a model change: it is data
    // about when the report was taken, and it is carried per row by ReadabilityReport.EvaluatedAt.
    //
    // v2 IS THE DATE-PRECISION CONVENTION. Chronology reads Experience.Period, and a period may now be
    // stated only to the month or the year; DateRange resolves such a period to the longest interval its
    // precision allows, so both the coverage a role contributes and the size of the gap after it are
    // answers this engine could not previously give. The convention and its error bound are on DateRange.
    //
    // The same two qualifications the scoring version carries apply here: no report that exists today
    // would score differently, because every persisted DateRange is full precision and the convention
    // reduces to the old arithmetic there, and yet a v2 report on an imported resume is explained by a
    // formula v1 did not have. Nothing REUSES a stored report -- EvaluateResumeReadability writes a row
    // per request and de-duplicates nothing -- so unlike the scoring bump this one costs no re-run; it
    // only stamps new rows honestly.
    public const int CurrentSchemaVersion = 2;

    public double Completeness { get; }
    public double Contact { get; }
    public double Achievements { get; }
    public double Chronology { get; }
    public double AtsParseability { get; }
    public int SchemaVersion { get; }

    private ReadabilityWeightsSnapshot(
        double completeness,
        double contact,
        double achievements,
        double chronology,
        double atsParseability,
        int schemaVersion)
    {
        Completeness = completeness;
        Contact = contact;
        Achievements = achievements;
        Chronology = chronology;
        AtsParseability = atsParseability;
        SchemaVersion = schemaVersion;
    }

    public static ReadabilityWeightsSnapshot Create(
        double completeness,
        double contact,
        double achievements,
        double chronology,
        double atsParseability,
        int schemaVersion = CurrentSchemaVersion)
    {
        // FINITE first, and this ordering matters. Every comparison below is false for NaN, so a NaN
        // weight passes the non-negative check AND passes the sum check (Math.Abs(NaN - 1.0) > 0.0001 is
        // false), and would be persisted as a snapshot whose arithmetic can never be reproduced.
        // Reachable, because RenormalizedTo divides.
        if (!double.IsFinite(completeness) || !double.IsFinite(contact) || !double.IsFinite(achievements)
            || !double.IsFinite(chronology) || !double.IsFinite(atsParseability))
            throw new ArgumentException("Weights must be finite numbers.");

        if (completeness < 0 || contact < 0 || achievements < 0 || chronology < 0 || atsParseability < 0)
            throw new ArgumentException("Weights must be non-negative.");

        // The invariant everything downstream leans on. It is what makes WeightedTotal a 0..1 number,
        // which is what makes ReadabilityReport.ReadabilityScore a percentage, which is what makes
        // ReadabilityBand's thresholds mean anything.
        var sum = completeness + contact + achievements + chronology + atsParseability;
        if (Math.Abs(sum - 1.0) > 0.0001)
            throw new ArgumentException($"Weights must sum to 1.0 (actual: {sum}).");

        if (schemaVersion < 1)
            throw new ArgumentException("SchemaVersion must be >= 1.", nameof(schemaVersion));

        return new ReadabilityWeightsSnapshot(
            completeness, contact, achievements, chronology, atsParseability, schemaVersion);
    }

    // The shipped weighting.
    //
    // Completeness leads because a section an ATS cannot find scores nothing at all downstream of it;
    // Achievements is next because it is the only section that measures what a candidate WROTE rather
    // than whether a field is filled in. AtsParseability is smallest because it is evidence about the
    // last document uploaded rather than about the CV as it stands, and today it applies to nothing --
    // see the remark on RenormalizedTo.
    public static ReadabilityWeightsSnapshot Default() => Create(0.30, 0.20, 0.25, 0.15, 0.10);

    // A SECTION THAT DOES NOT APPLY DOES NOT CONSUME WEIGHT.
    //
    // The same mechanism Scoring.ScoringWeightsSnapshot.RenormalizedTo implements, and the reason it is
    // needed here is AtsParseability: it can only be computed from evidence about the uploaded document,
    // and no resume carries that evidence yet. Left weighted at 0.10 against a score of 0.0 it would cap
    // every candidate at 0.90 -- ten points off everyone, for a question the product cannot ask them.
    // Renormalized out, the ceiling is 1.00 for every resume and the weight of 0 on the wire IS the
    // signal that the section measured nothing.
    //
    // Identity when every section applies: the divisor is 1.0, so each weight is returned bit for bit.
    //
    // SchemaVersion is CARRIED, NOT BUMPED. It names which readability MODEL produced the numbers; the
    // snapshot names the RESULT of applying that model to one resume. A v1 report is explained by "the v1
    // model, renormalized to what could be measured", and the row stores the actual divisor's output, so
    // the arithmetic is reproducible from the row alone.
    public ReadabilityWeightsSnapshot RenormalizedTo(IEnumerable<ReadabilitySectionType> applicableSections)
    {
        ArgumentNullException.ThrowIfNull(applicableSections);

        var applicable = new HashSet<ReadabilitySectionType>(applicableSections);

        // The degenerate case: nothing applies, or everything that does carries zero weight. There is no
        // renormalized set to return, and silently falling back to the unrenormalized weights would
        // reintroduce the unreachable ceiling this method exists to remove.
        var applicableTotal = applicable.Sum(WeightFor);
        if (applicableTotal <= 0.0)
            throw new ArgumentException(
                "At least one applicable section must carry weight.", nameof(applicableSections));

        double ShareFor(ReadabilitySectionType section) =>
            applicable.Contains(section) ? WeightFor(section) / applicableTotal : 0.0;

        // Create re-checks the sum, which is what makes this arithmetically sound rather than merely
        // plausible: the shares are w/T summed over exactly the sections that contributed T, so they
        // total 1.0 by construction, and the invariant catches it here if that ever stops being true.
        return Create(
            ShareFor(ReadabilitySectionType.Completeness),
            ShareFor(ReadabilitySectionType.Contact),
            ShareFor(ReadabilitySectionType.Achievements),
            ShareFor(ReadabilitySectionType.Chronology),
            ShareFor(ReadabilitySectionType.AtsParseability),
            SchemaVersion);
    }

    public double WeightFor(ReadabilitySectionType section) => section switch
    {
        ReadabilitySectionType.Completeness => Completeness,
        ReadabilitySectionType.Contact => Contact,
        ReadabilitySectionType.Achievements => Achievements,
        ReadabilitySectionType.Chronology => Chronology,
        ReadabilitySectionType.AtsParseability => AtsParseability,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown readability section.")
    };
}

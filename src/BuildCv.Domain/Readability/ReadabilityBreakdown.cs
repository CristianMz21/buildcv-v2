namespace BuildCv.Domain.Readability;

public sealed record ReadabilityBreakdown
{
    // Numeric order, which is the order Sections projects them in.
    private static readonly ReadabilitySectionType[] AllSections = Enum.GetValues<ReadabilitySectionType>();

    public double CompletenessScore { get; }
    public double ContactScore { get; }
    public double AchievementsScore { get; }
    public double ChronologyScore { get; }
    public double AtsParseabilityScore { get; }
    public ReadabilityWeightsSnapshot Weights { get; }

    private ReadabilityBreakdown(
        double completenessScore,
        double contactScore,
        double achievementsScore,
        double chronologyScore,
        double atsParseabilityScore,
        ReadabilityWeightsSnapshot weights)
    {
        CompletenessScore = completenessScore;
        ContactScore = contactScore;
        AchievementsScore = achievementsScore;
        ChronologyScore = chronologyScore;
        AtsParseabilityScore = atsParseabilityScore;
        Weights = weights;
    }

    public static ReadabilityBreakdown Create(
        double completenessScore,
        double contactScore,
        double achievementsScore,
        double chronologyScore,
        double atsParseabilityScore,
        ReadabilityWeightsSnapshot weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ValidateScore(completenessScore, nameof(completenessScore));
        ValidateScore(contactScore, nameof(contactScore));
        ValidateScore(achievementsScore, nameof(achievementsScore));
        ValidateScore(chronologyScore, nameof(chronologyScore));
        ValidateScore(atsParseabilityScore, nameof(atsParseabilityScore));
        return new ReadabilityBreakdown(
            completenessScore, contactScore, achievementsScore, chronologyScore, atsParseabilityScore, weights);
    }

    // FINITE first: `NaN < 0` and `NaN > 1` are both false, so a NaN score would pass the range check and
    // then poison WeightedTotal, the band, and the whole response -- one unguarded division upstream and
    // the candidate is shown nothing at all.
    private static void ValidateScore(double score, string paramName)
    {
        if (!double.IsFinite(score))
            throw new ArgumentException("Score must be a finite number.", paramName);
        if (score < 0 || score > 1)
            throw new ArgumentException("Score must be between 0 and 1.", paramName);
    }

    public double WeightedTotal =>
        Weights.Completeness * CompletenessScore +
        Weights.Contact * ContactScore +
        Weights.Achievements * AchievementsScore +
        Weights.Chronology * ChronologyScore +
        Weights.AtsParseability * AtsParseabilityScore;

    // The five stored doubles paired with the weights they were counted under, so a caller never has to
    // pair a score with a weight by hand and cannot pair it with the wrong snapshot's.
    //
    // COMPUTED, and the persistence layer Ignores it. Left mapped, EF discovers ReadabilitySectionScore
    // as an entity type and the model build fails somewhere far from here.
    public IReadOnlyList<ReadabilitySectionScore> Sections =>
        [.. AllSections.Select(section =>
            ReadabilitySectionScore.Create(section, ScoreFor(section), Weights.WeightFor(section)))];

    // THE enum-to-column switch, deliberately the only one. Every consumer that wants "the score for this
    // section" reads through here, so adding a ReadabilitySectionType member without a column to back it
    // fails loudly in one place instead of quietly reading zero in several.
    public double ScoreFor(ReadabilitySectionType section) => section switch
    {
        ReadabilitySectionType.Completeness => CompletenessScore,
        ReadabilitySectionType.Contact => ContactScore,
        ReadabilitySectionType.Achievements => AchievementsScore,
        ReadabilitySectionType.Chronology => ChronologyScore,
        ReadabilitySectionType.AtsParseability => AtsParseabilityScore,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown readability section.")
    };
}

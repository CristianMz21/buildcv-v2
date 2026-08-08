using BuildCv.Domain.Readability;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Readability;

// A section that could not be measured must not consume weight. These pin the arithmetic; that the
// ENGINE picks the right applicable set is pinned in ReadabilityEngineTests.
public class ReadabilityWeightsSnapshotTests
{
    private static readonly ReadabilityWeightsSnapshot Default = ReadabilityWeightsSnapshot.Default();

    private static readonly ReadabilitySectionType[] AllSections = Enum.GetValues<ReadabilitySectionType>();

    private static readonly ReadabilitySectionType[] WithoutAtsParseability =
    [
        ReadabilitySectionType.Completeness,
        ReadabilitySectionType.Contact,
        ReadabilitySectionType.Achievements,
        ReadabilitySectionType.Chronology,
    ];

    [Fact]
    public void Default_sums_to_one()
    {
        AllSections.Sum(Default.WeightFor).Should().BeApproximately(1.0, 1e-12);
    }

    // The identity case, and the one that makes renormalization safe to reason about: a resume every
    // section applies to divides by 1.0, so it is scored under exactly the shipped weights. Exact
    // equality, not approximate — `w / 1.0` is `w` in IEEE 754, so anything looser would tolerate a
    // divisor that was not really 1.
    [Fact]
    public void Renormalizing_over_every_section_returns_the_same_weights()
    {
        Default.RenormalizedTo(AllSections).Should().Be(Default);
    }

    // THE CASE EVERY REPORT THIS BUILD PRODUCES TAKES. AtsParseability needs evidence about the uploaded
    // document, and no resume carries any, so it is renormalized out of every run.
    [Fact]
    public void Renormalizing_without_ats_parseability_drops_it_to_zero_and_the_rest_still_sum_to_one()
    {
        var renormalized = Default.RenormalizedTo(WithoutAtsParseability);

        renormalized.AtsParseability.Should().Be(0.0);
        AllSections.Sum(renormalized.WeightFor).Should().BeApproximately(1.0, 1e-12,
            "the ceiling has to stay 1.00 or every candidate is capped at 0.90 for a question we cannot ask");
    }

    // Proportional, not equal. The four sections that survive keep their ratios to one another — a
    // redistribution that simply split the freed weight evenly would satisfy the sum invariant and every
    // ceiling test, and would silently re-rank the sections against each other.
    [Fact]
    public void Renormalizing_preserves_the_ratios_between_the_sections_that_remain()
    {
        var renormalized = Default.RenormalizedTo(WithoutAtsParseability);

        // 0.30 / 0.90, 0.20 / 0.90, 0.25 / 0.90, 0.15 / 0.90.
        renormalized.Completeness.Should().BeApproximately(0.30 / 0.90, 1e-12);
        renormalized.Contact.Should().BeApproximately(0.20 / 0.90, 1e-12);
        renormalized.Achievements.Should().BeApproximately(0.25 / 0.90, 1e-12);
        renormalized.Chronology.Should().BeApproximately(0.15 / 0.90, 1e-12);

        (renormalized.Completeness / renormalized.Chronology)
            .Should().BeApproximately(Default.Completeness / Default.Chronology, 1e-12,
                "Completeness was worth two Chronologies before and must be worth two after");
    }

    // Exercised over all 32 subsets rather than the one the engine can produce today, so the mechanism is
    // proved in both directions before T3.5 makes the other direction reachable.
    [Fact]
    public void Renormalizing_over_any_subset_that_carries_weight_still_sums_to_one()
    {
        foreach (var subset in Subsets(AllSections))
        {
            if (subset.Sum(Default.WeightFor) <= 0.0)
                continue;

            var renormalized = Default.RenormalizedTo(subset);

            AllSections.Sum(renormalized.WeightFor).Should().BeApproximately(1.0, 0.0001,
                $"the subset [{string.Join(", ", subset)}] must still be a complete weighting");

            foreach (var section in AllSections.Except(subset))
                renormalized.WeightFor(section).Should().Be(0.0);
        }
    }

    [Fact]
    public void Renormalizing_over_nothing_throws_rather_than_falling_back()
    {
        var act = () => Default.RenormalizedTo([]);

        act.Should().Throw<ArgumentException>().WithParameterName("applicableSections");
    }

    [Fact]
    public void Renormalizing_rejects_a_null_section_list()
    {
        var act = () => Default.RenormalizedTo(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // SchemaVersion is CARRIED, not bumped. It names which readability MODEL produced the numbers; the
    // snapshot names the RESULT of applying that model to one resume.
    [Fact]
    public void Renormalizing_carries_the_schema_version_rather_than_bumping_it()
    {
        var renormalized = Default.RenormalizedTo(WithoutAtsParseability);

        renormalized.SchemaVersion.Should().Be(Default.SchemaVersion);
        renormalized.SchemaVersion.Should().Be(ReadabilityWeightsSnapshot.CurrentSchemaVersion);
    }

    // The consequence of the line above, stated where someone will look for it: a persisted v1 snapshot
    // is NOT necessarily Default(). Anything deciding "was this produced by the current model" has to
    // read SchemaVersion, not compare the weights — and on this engine the renormalized set is what
    // every row holds, so comparing against Default() would answer "no" for all of them.
    [Fact]
    public void A_renormalized_snapshot_is_not_the_default_but_still_claims_the_current_version()
    {
        var renormalized = Default.RenormalizedTo(WithoutAtsParseability);

        renormalized.Should().NotBe(Default);
        renormalized.SchemaVersion.Should().Be(ReadabilityWeightsSnapshot.CurrentSchemaVersion);
    }

    // FINITE FIRST, and the ordering is what makes it work: every comparison in Create is false for NaN,
    // so a NaN weight passes the non-negative check AND the sum check unchallenged.
    [Fact]
    public void Create_rejects_a_non_finite_weight()
    {
        var act = () => ReadabilityWeightsSnapshot.Create(double.NaN, 0.20, 0.25, 0.15, 0.10);

        act.Should().Throw<ArgumentException>().WithMessage("*finite*");
    }

    [Fact]
    public void Create_rejects_a_negative_weight()
    {
        var act = () => ReadabilityWeightsSnapshot.Create(-0.10, 0.30, 0.35, 0.25, 0.20);

        act.Should().Throw<ArgumentException>().WithMessage("*non-negative*");
    }

    [Fact]
    public void Create_rejects_weights_that_do_not_sum_to_one()
    {
        var act = () => ReadabilityWeightsSnapshot.Create(0.30, 0.20, 0.25, 0.15, 0.20);

        act.Should().Throw<ArgumentException>().WithMessage("*sum to 1.0*");
    }

    [Fact]
    public void Create_rejects_a_schema_version_below_one()
    {
        var act = () => ReadabilityWeightsSnapshot.Create(0.30, 0.20, 0.25, 0.15, 0.10, schemaVersion: 0);

        act.Should().Throw<ArgumentException>().WithParameterName("schemaVersion");
    }

    [Fact]
    public void WeightFor_rejects_a_section_that_is_not_a_member()
    {
        var act = () => Default.WeightFor((ReadabilitySectionType)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static IEnumerable<ReadabilitySectionType[]> Subsets(ReadabilitySectionType[] sections)
    {
        for (var mask = 0; mask < 1 << sections.Length; mask++)
        {
            yield return [.. sections.Where((_, index) => (mask & (1 << index)) != 0)];
        }
    }
}

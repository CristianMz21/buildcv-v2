using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

// A section the posting asks nothing of must not consume weight. These pin the arithmetic; that the
// ENGINE picks the right applicable set is pinned in ScoringEngineTests.
public class ScoringWeightsRenormalizationTests
{
    private static readonly ScoringWeightsSnapshot Default = ScoringWeightsSnapshot.Default();

    private static readonly SectionType[] AllSections = Enum.GetValues<SectionType>();

    // The identity case, and the one that makes the change safe to reason about: a posting that asks
    // about everything divides by 1.0, so a fully-specified posting is scored under exactly the shipped
    // weights and nothing about it moved. Exact equality, not approximate — `w / 1.0` is `w` in IEEE
    // 754, so anything looser would tolerate a divisor that was not really 1.
    [Fact]
    public void Renormalizing_over_every_section_returns_the_same_weights()
    {
        Default.RenormalizedTo(AllSections).Should().Be(Default);
    }

    [Fact]
    public void Renormalizing_drops_the_sections_that_do_not_apply_to_zero()
    {
        var renormalized = Default.RenormalizedTo(
            [SectionType.Experience, SectionType.Education, SectionType.Certifications, SectionType.Projects]);

        renormalized.Skills.Should().Be(0.0);
        renormalized.Languages.Should().Be(0.0);
    }

    // Proportional, not equal. The four sections that survive keep their ratios to one another — a
    // redistribution that simply split the freed weight evenly would satisfy the sum invariant and
    // every ceiling test, and would silently re-rank the sections against each other.
    [Fact]
    public void Renormalizing_preserves_the_ratios_between_the_sections_that_remain()
    {
        var renormalized = Default.RenormalizedTo(
            [SectionType.Experience, SectionType.Education, SectionType.Certifications, SectionType.Projects]);

        // 0.20 / 0.45, 0.10 / 0.45, 0.10 / 0.45, 0.05 / 0.45.
        renormalized.Experience.Should().BeApproximately(0.20 / 0.45, 1e-12);
        renormalized.Education.Should().BeApproximately(0.10 / 0.45, 1e-12);
        renormalized.Certifications.Should().BeApproximately(0.10 / 0.45, 1e-12);
        renormalized.Projects.Should().BeApproximately(0.05 / 0.45, 1e-12);

        (renormalized.Experience / renormalized.Projects)
            .Should().BeApproximately(Default.Experience / Default.Projects, 1e-12,
                "Experience was worth four Projects before and must be worth four Projects after");
    }

    // THE INVARIANT the whole design rests on. Every subset that carries weight must produce a set that
    // sums to 1.0, because that is what makes WeightedTotal a 0..1 number, which is what makes
    // OverallScore a percentage. Exercised over all 64 subsets rather than the four the engine can
    // actually produce, so a future section becoming optional is covered before it is written.
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

    // Unreachable today — Experience, Education, Certifications and Projects always apply and carry 0.45
    // between them — but there is no renormalized set to return, and falling back to the unrenormalized
    // weights would quietly reintroduce the unreachable ceiling this method exists to remove.
    [Fact]
    public void Renormalizing_over_nothing_throws_rather_than_falling_back()
    {
        var act = () => Default.RenormalizedTo([]);

        act.Should().Throw<ArgumentException>().WithParameterName("applicableSections");
    }

    [Fact]
    public void Renormalizing_over_only_zero_weighted_sections_throws()
    {
        // Languages at 0.0 is exactly the v1 shape, so this is a set a persisted snapshot really can be
        // renormalized from — not a hypothetical.
        var v1 = ScoringWeightsSnapshot.Create(0.45, 0.20, 0.20, 0.10, 0.05, 0.00, schemaVersion: 1);

        var act = () => v1.RenormalizedTo([SectionType.Languages]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Renormalizing_rejects_a_null_section_list()
    {
        var act = () => Default.RenormalizedTo(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // SchemaVersion is CARRIED, not bumped. It names which weighting RULE produced the numbers; the
    // snapshot names the RESULT. A renormalized v2 set is still v2 applied to a posting, and the row
    // stores the divisor's output, so the arithmetic is reproducible from the row alone.
    [Fact]
    public void Renormalizing_carries_the_schema_version_rather_than_bumping_it()
    {
        var renormalized = Default.RenormalizedTo([SectionType.Experience, SectionType.Skills]);

        renormalized.SchemaVersion.Should().Be(Default.SchemaVersion);
        renormalized.SchemaVersion.Should().Be(ScoringWeightsSnapshot.CurrentSchemaVersion);
    }

    // The consequence of the line above, stated where someone will look for it: a persisted v2 snapshot
    // is NO LONGER NECESSARILY Default(). Anything deciding "was this scored under the current model"
    // has to read SchemaVersion, not compare the weights.
    [Fact]
    public void A_renormalized_snapshot_is_not_the_default_but_still_claims_the_current_version()
    {
        var renormalized = Default.RenormalizedTo(
            [SectionType.Experience, SectionType.Education, SectionType.Certifications, SectionType.Projects]);

        renormalized.Should().NotBe(Default);
        renormalized.SchemaVersion.Should().Be(ScoringWeightsSnapshot.CurrentSchemaVersion);
    }

    private static IEnumerable<SectionType[]> Subsets(SectionType[] sections)
    {
        for (var mask = 0; mask < 1 << sections.Length; mask++)
            yield return [.. sections.Where((_, index) => (mask & (1 << index)) != 0)];
    }
}

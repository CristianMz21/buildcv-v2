using BuildCv.Domain.Readability;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Readability;

// The order a candidate reads their advice in, applied in two layers — the Application layer sorts
// before persisting to decide which ten survive the cap, the Api layer sorts again because the child
// table is an honest SET. One copy of the comparisons, so the two cannot disagree.
public class ReadabilityRecommendationOrderTests
{
    [Fact]
    public void Priority_ascends_so_critical_advice_is_read_first()
    {
        var niceToHave = Advice(RecommendationPriority.NiceToHave, impact: 0.9);
        var critical = Advice(RecommendationPriority.Critical, impact: 0.01);

        // Priority outranks impact: a Critical worth 0.01 is still read before a NiceToHave worth 0.9.
        ReadabilityRecommendationOrder.Sort([niceToHave, critical]).Should().Equal(critical, niceToHave);
    }

    [Fact]
    public void Impact_descends_within_a_priority()
    {
        var small = Advice(RecommendationPriority.Important, impact: 0.04);
        var large = Advice(RecommendationPriority.Important, impact: 0.08);

        ReadabilityRecommendationOrder.Sort([small, large]).Should().Equal(large, small);
    }

    [Fact]
    public void Section_ascends_when_priority_and_impact_tie()
    {
        var chronology = Advice(section: ReadabilitySectionType.Chronology);
        var completeness = Advice(section: ReadabilitySectionType.Completeness);

        ReadabilityRecommendationOrder.Sort([chronology, completeness])
            .Should().Equal(completeness, chronology);
    }

    // The last resort, and it exists to make the order TOTAL rather than because alphabetical advice
    // means anything: a partial order leaves ties to whatever the sort happened to do, so two entries
    // tied on everything else would take turns being the tenth.
    [Fact]
    public void Message_breaks_the_last_tie_ordinally()
    {
        var second = Advice(message: "Bravo");
        var first = Advice(message: "Alpha");

        ReadabilityRecommendationOrder.Sort([second, first]).Should().Equal(first, second);
    }

    [Fact]
    public void Sort_rejects_a_null_list()
    {
        var act = () => ReadabilityRecommendationOrder.Sort(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static ReadabilityRecommendation Advice(
        RecommendationPriority priority = RecommendationPriority.Important,
        double impact = 0.05,
        ReadabilitySectionType section = ReadabilitySectionType.Contact,
        string message = "Advice.") =>
        ReadabilityRecommendation.Create(
            section, priority, ReadabilityRecommendationKind.NoPhoneNumber, message, impact);
}

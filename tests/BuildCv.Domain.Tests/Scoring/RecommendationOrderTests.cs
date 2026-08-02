using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

// The four keys of the display order, one test each, because both DIRECTIONS are easy to get backwards
// and the two differ: Priority ascends (Critical is 0) while Impact descends (biggest recovery first).
// A single "it sorts" test over a mixed list would pass with either one inverted as long as the other
// dominated.
public class RecommendationOrderTests
{
    [Fact]
    public void Priority_ascends_so_critical_advice_is_read_first()
    {
        var sorted = RecommendationOrder.Sort(
        [
            Advice(RecommendationPriority.NiceToHave, impact: 0.90),
            Advice(RecommendationPriority.Critical, impact: 0.01),
            Advice(RecommendationPriority.Important, impact: 0.50),
        ]);

        sorted.Select(r => r.Priority).Should().Equal(
            RecommendationPriority.Critical,
            RecommendationPriority.Important,
            RecommendationPriority.NiceToHave);
    }

    // Deliberately the losing impact on the winning priority: Critical at 0.01 outranks NiceToHave at
    // 0.90 above, which is what makes Priority the outer key rather than a tiebreak on Impact.
    [Fact]
    public void Impact_descends_within_a_priority_so_the_biggest_recovery_comes_first()
    {
        var sorted = RecommendationOrder.Sort(
        [
            Advice(RecommendationPriority.Critical, impact: 0.10),
            Advice(RecommendationPriority.Critical, impact: 0.40),
            Advice(RecommendationPriority.Critical, impact: 0.25),
        ]);

        sorted.Select(r => r.Impact).Should().Equal(0.40, 0.25, 0.10);
    }

    [Fact]
    public void Section_breaks_a_tie_on_priority_and_impact()
    {
        var sorted = RecommendationOrder.Sort(
        [
            Advice(RecommendationPriority.Critical, 0.20, SectionType.Languages),
            Advice(RecommendationPriority.Critical, 0.20, SectionType.Skills),
            Advice(RecommendationPriority.Critical, 0.20, SectionType.Education),
        ]);

        sorted.Select(r => r.Section).Should().Equal(
            SectionType.Skills, SectionType.Education, SectionType.Languages);
    }

    // The last resort, and the reason the order is TOTAL rather than merely well-defined most of the
    // time. Ordinal, not culture-aware: a sort that changed with the server's locale would make the
    // persisted set and the response disagree between two machines running the same build.
    [Fact]
    public void Message_breaks_the_last_tie_ordinally()
    {
        var sorted = RecommendationOrder.Sort(
        [
            Advice(RecommendationPriority.Critical, 0.20, SectionType.Skills, "Add Zookeeper."),
            Advice(RecommendationPriority.Critical, 0.20, SectionType.Skills, "Add Ada."),
            Advice(RecommendationPriority.Critical, 0.20, SectionType.Skills, "Add Maven."),
        ]);

        sorted.Select(r => r.Message).Should().Equal("Add Ada.", "Add Maven.", "Add Zookeeper.");
    }

    // A total order is one no input can leave ambiguous. Sorting the same set from two different
    // starting arrangements has to give the same sequence, or the cap at ten would hand a candidate a
    // different tenth recommendation depending on what order the rules happened to run in.
    [Fact]
    public void Sort_gives_the_same_sequence_whatever_order_it_receives()
    {
        List<Recommendation> advice =
        [
            Advice(RecommendationPriority.Critical, 0.20, SectionType.Skills, "Add Ada."),
            Advice(RecommendationPriority.Critical, 0.20, SectionType.Skills, "Add Maven."),
            Advice(RecommendationPriority.Important, 0.05, SectionType.Projects, "Add a project."),
            Advice(RecommendationPriority.Critical, 0.30, SectionType.Education, "Add your education."),
        ];

        var forwards = RecommendationOrder.Sort(advice).Select(r => r.Message);
        var backwards = RecommendationOrder.Sort(Enumerable.Reverse(advice)).Select(r => r.Message);

        backwards.Should().Equal(forwards);
    }

    [Fact]
    public void Sort_rejects_a_null_source()
    {
        var act = () => RecommendationOrder.Sort(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static Recommendation Advice(
        RecommendationPriority priority,
        double impact,
        SectionType section = SectionType.Skills,
        string message = "Add SQL.") =>
        Recommendation.Create(section, priority, RecommendationKind.MissingMustHaveSkill, message, impact);
}

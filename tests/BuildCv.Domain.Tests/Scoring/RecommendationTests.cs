using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

public class RecommendationTests
{
    [Fact]
    public void Create_keeps_the_structure_and_the_sentence()
    {
        var recommendation = Recommendation.Create(
            SectionType.Skills,
            RecommendationPriority.Critical,
            RecommendationKind.MissingMustHaveSkill,
            "Add Kubernetes to your skills.",
            0.45);

        recommendation.Section.Should().Be(SectionType.Skills);
        recommendation.Priority.Should().Be(RecommendationPriority.Critical);
        recommendation.Kind.Should().Be(RecommendationKind.MissingMustHaveSkill);
        recommendation.Message.Should().Be("Add Kubernetes to your skills.");
        recommendation.Impact.Should().Be(0.45);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_message(string? message)
    {
        var act = () => Build(message!, 0.5);

        act.Should().Throw<InvalidRecommendationException>();
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Create_rejects_an_impact_outside_the_unit_interval(double impact)
    {
        var act = () => Build("Add Kubernetes.", impact);

        act.Should().Throw<InvalidRecommendationException>();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Create_accepts_the_endpoints_of_the_unit_interval(double impact)
    {
        Build("Add Kubernetes.", impact).Impact.Should().Be(impact);
    }

    [Fact]
    public void Create_trims_the_message()
    {
        Build("  Add Kubernetes.  ", 0.5).Message.Should().Be("Add Kubernetes.");
    }

    // The kind is what "which advice do we give most often" groups by, so two recommendations that
    // differ only there are genuinely different advice.
    [Fact]
    public void Recommendations_with_the_same_sentence_but_different_kinds_are_not_equal()
    {
        var mustHave = Recommendation.Create(
            SectionType.Skills, RecommendationPriority.Critical, RecommendationKind.MissingMustHaveSkill, "Add C#.", 0.4);
        var niceToHave = Recommendation.Create(
            SectionType.Skills, RecommendationPriority.Critical, RecommendationKind.MissingNiceToHaveSkill, "Add C#.", 0.4);

        mustHave.Should().NotBe(niceToHave);
    }

    // Persisted as tinyint. Renumbering a member rewrites the meaning of every row already on disk,
    // so the numbers are pinned here rather than left to declaration order.
    [Theory]
    [InlineData(RecommendationKind.MissingMustHaveSkill, 0)]
    [InlineData(RecommendationKind.MissingNiceToHaveSkill, 1)]
    [InlineData(RecommendationKind.NoEducationRecorded, 2)]
    [InlineData(RecommendationKind.NoDegreeRecorded, 3)]
    [InlineData(RecommendationKind.FewerCertificationsThanExpected, 4)]
    [InlineData(RecommendationKind.FewerProjectsThanExpected, 5)]
    [InlineData(RecommendationKind.LanguageMissing, 6)]
    [InlineData(RecommendationKind.LanguageBelowRequiredLevel, 7)]
    [InlineData(RecommendationKind.LanguageLevelNotRecorded, 8)]
    [InlineData(RecommendationKind.ExperienceNotMarkedProfessional, 9)]
    public void RecommendationKind_members_keep_their_persisted_numbers(RecommendationKind kind, int expected) =>
        ((int)kind).Should().Be(expected);

    [Theory]
    [InlineData(RecommendationPriority.Critical, 0)]
    [InlineData(RecommendationPriority.Important, 1)]
    [InlineData(RecommendationPriority.NiceToHave, 2)]
    public void RecommendationPriority_members_keep_their_persisted_numbers(
        RecommendationPriority priority, int expected) =>
        ((int)priority).Should().Be(expected);

    [Theory]
    [InlineData(SectionType.Skills, 0)]
    [InlineData(SectionType.Experience, 1)]
    [InlineData(SectionType.Education, 2)]
    [InlineData(SectionType.Certifications, 3)]
    [InlineData(SectionType.Projects, 4)]
    [InlineData(SectionType.Languages, 5)]
    public void SectionType_members_keep_their_persisted_numbers(SectionType section, int expected) =>
        ((int)section).Should().Be(expected);

    private static Recommendation Build(string message, double impact) =>
        Recommendation.Create(
            SectionType.Skills, RecommendationPriority.Important, RecommendationKind.MissingMustHaveSkill,
            message, impact);
}

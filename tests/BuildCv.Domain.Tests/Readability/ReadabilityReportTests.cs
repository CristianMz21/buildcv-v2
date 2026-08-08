using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Readability;

public class ReadabilityReportTests
{
    private static readonly ReadabilityWeightsSnapshot Default = ReadabilityWeightsSnapshot.Default();

    private static ReadabilityReport Report(
        double weightedTotalTarget, IReadOnlyList<ReadabilityRecommendation>? recommendations = null) =>
        ReadabilityReport.Create(
            ReadabilityReportId.New(),
            // Every section at the same score, so the weighted total is that score exactly: the weights
            // sum to 1.0 by invariant. That is what lets a band test name a total rather than solve for
            // one.
            ReadabilityBreakdown.Create(
                weightedTotalTarget, weightedTotalTarget, weightedTotalTarget,
                weightedTotalTarget, weightedTotalTarget, Default),
            ResumeId.New(),
            DateTimeOffset.UtcNow,
            recommendations);

    [Fact]
    public void ReadabilityScore_is_the_weighted_total_as_a_percentage()
    {
        Report(0.735).ReadabilityScore.Should().Be(74, "0.735 * 100 rounds to 74");
    }

    // The four bands and both sides of every threshold. Named ReadabilityScore rather than OverallScore
    // deliberately: OverallScore means "match against this posting" and the two are on the same 0..100
    // scale, so one name over both is how a client ends up charting them against each other.
    [Theory]
    [InlineData(0.00, ReadabilityBand.Low)]
    [InlineData(0.39, ReadabilityBand.Low)]
    [InlineData(0.40, ReadabilityBand.Medium)]
    [InlineData(0.59, ReadabilityBand.Medium)]
    [InlineData(0.60, ReadabilityBand.Good)]
    [InlineData(0.79, ReadabilityBand.Good)]
    [InlineData(0.80, ReadabilityBand.Strong)]
    [InlineData(1.00, ReadabilityBand.Strong)]
    public void Band_is_decided_by_the_readability_score(double total, ReadabilityBand expected)
    {
        Report(total).Band.Should().Be(expected);
    }

    [Fact]
    public void Create_defaults_to_no_recommendations_rather_than_null()
    {
        Report(0.5).Recommendations.Should().BeEmpty();
    }

    // The collection is COPIED, so a caller holding the list it passed in cannot change what the report
    // says afterwards.
    [Fact]
    public void Create_copies_the_recommendations_it_is_given()
    {
        List<ReadabilityRecommendation> source = [Advice()];
        var report = Report(0.5, source);

        source.Add(Advice());

        report.Recommendations.Should().HaveCount(1);
    }

    [Fact]
    public void Create_rejects_a_null_id_breakdown_or_resume_id()
    {
        var breakdown = ReadabilityBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, Default);

        var withoutId = () => ReadabilityReport.Create(
            null!, breakdown, ResumeId.New(), DateTimeOffset.UtcNow);
        var withoutBreakdown = () => ReadabilityReport.Create(
            ReadabilityReportId.New(), null!, ResumeId.New(), DateTimeOffset.UtcNow);
        var withoutResume = () => ReadabilityReport.Create(
            ReadabilityReportId.New(), breakdown, null!, DateTimeOffset.UtcNow);

        withoutId.Should().Throw<ArgumentNullException>();
        withoutBreakdown.Should().Throw<ArgumentNullException>();
        withoutResume.Should().Throw<ArgumentNullException>();
    }

    // Identity, not value: two reports of the same resume taken a second apart are different facts.
    [Fact]
    public void Two_reports_are_equal_only_when_they_share_an_id()
    {
        var report = Report(0.5);

        report.Equals(Report(0.5)).Should().BeFalse();
        report.Equals(report).Should().BeTrue();
    }

    [Fact]
    public void ReadabilityReportId_rejects_an_empty_guid()
    {
        var act = () => new ReadabilityReportId(Guid.Empty);

        act.Should().Throw<ArgumentException>().WithParameterName("value");
    }

    private static ReadabilityRecommendation Advice() =>
        ReadabilityRecommendation.Create(
            ReadabilitySectionType.Contact,
            RecommendationPriority.Important,
            ReadabilityRecommendationKind.NoPhoneNumber,
            "Add a phone number.",
            0.05);
}

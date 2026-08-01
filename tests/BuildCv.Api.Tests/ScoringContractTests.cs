using System.Text.Json;
using BuildCv.Api.Contracts;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Api.Tests;

// The wire contract, asserted against the mapper directly so the shape can be pinned without a live
// request — and so the SORT can be tested at all. Through the endpoint the recommendations arrive
// already ordered (the Application layer sorts before persisting, to decide which ten survive the
// cap), so a live response cannot tell a mapper that sorts from one that does not. Analysis is a set
// on reload, which is exactly the case this covers.
public class ScoringContractTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void From_SortsRecommendationsIntoTheTotalOrder()
    {
        // Deliberately scrambled: a NiceToHave first, then two Criticals whose impacts are the wrong way
        // round, then an Important. Reload order is the server's choice, so this is a shape the mapper
        // really receives.
        var analysis = BuildAnalysis(
            Advice(SectionType.Projects, RecommendationPriority.NiceToHave,
                RecommendationKind.FewerProjectsThanExpected, "Add a project.", 0.017),
            Advice(SectionType.Skills, RecommendationPriority.Critical,
                RecommendationKind.MissingMustHaveSkill, "Add SQL.", 0.15),
            Advice(SectionType.Education, RecommendationPriority.Critical,
                RecommendationKind.NoEducationRecorded, "Add your education.", 0.30),
            Advice(SectionType.Certifications, RecommendationPriority.Important,
                RecommendationKind.FewerCertificationsThanExpected, "Add a certification.", 0.033));

        var response = AnalysisResponse.From(analysis);

        response.Recommendations.Select(r => r.Kind).Should().Equal(
            nameof(RecommendationKind.NoEducationRecorded),
            nameof(RecommendationKind.MissingMustHaveSkill),
            nameof(RecommendationKind.FewerCertificationsThanExpected),
            nameof(RecommendationKind.FewerProjectsThanExpected));
    }

    // Ties on priority and impact are broken by Section, then by Message. Without both, two pieces of
    // advice worth the same amount would take turns being first between two reads of the same analysis.
    [Fact]
    public void From_BreaksTiesBySectionThenMessage()
    {
        var analysis = BuildAnalysis(
            Advice(SectionType.Projects, RecommendationPriority.Critical,
                RecommendationKind.FewerProjectsThanExpected, "B project.", 0.20),
            Advice(SectionType.Skills, RecommendationPriority.Critical,
                RecommendationKind.MissingMustHaveSkill, "B skill.", 0.20),
            Advice(SectionType.Skills, RecommendationPriority.Critical,
                RecommendationKind.MissingMustHaveSkill, "A skill.", 0.20));

        var response = AnalysisResponse.From(analysis);

        response.Recommendations.Select(r => r.Message).Should().Equal("A skill.", "B skill.", "B project.");
    }

    // The whole point of the DTO: these three names would otherwise ship as 0, 0 and 0 off the
    // aggregate, and the numbers are documented in three files as an append-only PERSISTENCE detail.
    [Fact]
    public void Serialized_NewFieldsCarryEnumNamesNotNumbers()
    {
        var analysis = BuildAnalysis(
            Advice(SectionType.Skills, RecommendationPriority.Critical,
                RecommendationKind.MissingMustHaveSkill, "Add SQL.", 0.15));

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(AnalysisResponse.From(analysis), WebOptions));

        var recommendation = json.RootElement.GetProperty("recommendations")[0];
        recommendation.GetProperty("section").GetString().Should().Be("Skills");
        recommendation.GetProperty("priority").GetString().Should().Be("Critical");
        recommendation.GetProperty("kind").GetString().Should().Be("MissingMustHaveSkill");
        recommendation.GetProperty("impact").GetDouble().Should().Be(0.15);
    }

    // The pre-existing shape, reproduced verbatim rather than tidied. Every assertion here is a
    // deliberate inconsistency with the one above, and each is cheaper than breaking a client.
    [Fact]
    public void Serialized_PreExistingFieldsKeepTheirOldEncoding()
    {
        var analysis = BuildAnalysis();

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(AnalysisResponse.From(analysis), WebOptions));
        var root = json.RootElement;

        root.GetProperty("band").ValueKind.Should().Be(JsonValueKind.Number,
            "flipping ScoreBand to a string is a repo-wide change, not a scoring one");
        root.GetProperty("id").GetProperty("value").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("resumeId").GetProperty("value").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("jobPostingId").GetProperty("value").ValueKind.Should().Be(JsonValueKind.String);

        root.GetProperty("breakdown").GetProperty("sections")[0]
            .GetProperty("section").ValueKind.Should().Be(JsonValueKind.Number,
                "the same enum is a name on a recommendation and a number here, and that is the price "
                + "of not moving a field this endpoint already ships");
    }

    // The full property list, in order, at both levels. A field silently disappearing is the failure a
    // per-field assertion cannot see, and reproducing the old response verbatim is this PR's claim.
    [Fact]
    public void Serialized_CarriesExactlyTheFieldsTheEndpointAlreadyShipped()
    {
        var analysis = BuildAnalysis();

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(AnalysisResponse.From(analysis), WebOptions));
        var root = json.RootElement;

        NamesOf(root).Should().Equal(
            "id", "breakdown", "resumeId", "jobPostingId", "scoredAt", "recommendations",
            "overallScore", "band");

        NamesOf(root.GetProperty("breakdown")).Should().Equal(
            "skillsScore", "experienceScore", "educationScore", "certificationsScore", "projectsScore",
            "languagesScore", "weights", "weightedTotal", "sections");

        NamesOf(root.GetProperty("breakdown").GetProperty("weights")).Should().Equal(
            "skills", "experience", "education", "certifications", "projects", "languages",
            "schemaVersion");

        NamesOf(root.GetProperty("breakdown").GetProperty("sections")[0]).Should().Equal(
            "section", "score", "weight");
    }

    private static IEnumerable<string> NamesOf(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    private static Recommendation Advice(
        SectionType section, RecommendationPriority priority, RecommendationKind kind,
        string message, double impact) =>
        Recommendation.Create(section, priority, kind, message, impact);

    private static Analysis BuildAnalysis(params Recommendation[] recommendations) =>
        Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.5, 0.4, 0.3, 0.2, 0.1, 0.6, ScoringWeightsSnapshot.Default()),
            ResumeId.New(),
            JobPostingId.New(),
            DateTimeOffset.UtcNow,
            recommendations);
}

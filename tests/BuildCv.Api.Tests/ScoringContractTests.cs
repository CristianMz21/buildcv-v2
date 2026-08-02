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

    // Four pre-chain fields keep their old encoding, and a fifth deliberately does NOT — asserted in the
    // same test so the exception cannot be read off as an omission. `band`, `id`, `resumeId` and
    // `jobPostingId` are deliberate inconsistencies with the named enums above, each cheaper than
    // breaking a client. `recommendations` is the opposite trade and the point of the release: pre-chain
    // Analysis.Recommendations was IReadOnlyList<string>, so the field shipped as string[], and it is an
    // array of OBJECTS now. Every other assertion in this file is about a field this chain added; without
    // the last one, the file would document a response as unchanged while changing a client's type.
    //
    // Measured, not assumed: shipping recommendations as strings again (a JsonConverter writing
    // v.Message) fails SIX tests, this one among them. So the last line is not the encoding's only
    // guard — it is what makes this test's own stated scope true, which is the defect it closes.
    [Fact]
    public void Serialized_PreChainFieldsKeepTheirOldEncodingExceptRecommendations()
    {
        var analysis = BuildAnalysis(
            Advice(SectionType.Skills, RecommendationPriority.Critical,
                RecommendationKind.MissingMustHaveSkill, "Add SQL.", 0.15));

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(AnalysisResponse.From(analysis), WebOptions));
        var root = json.RootElement;

        root.GetProperty("band").ValueKind.Should().Be(JsonValueKind.Number,
            "flipping ScoreBand to a string is a repo-wide change, not a scoring one");
        root.GetProperty("id").GetProperty("value").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("resumeId").GetProperty("value").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("jobPostingId").GetProperty("value").ValueKind.Should().Be(JsonValueKind.String);

        root.GetProperty("recommendations")[0].ValueKind.Should().Be(JsonValueKind.Object,
            "the one pre-chain field whose wire type this chain changes — it was string[] and a typed "
            + "client breaks on the first non-empty array");
    }

    // Both arrays that carry a SectionType name it. `sections[]` looks pre-existing and is not — it was
    // added by PR 1, is unmerged and has no clients, so it belongs to this chain and was corrected while
    // that was still free. One enum, one encoding, in the DTO whose whole purpose is stopping those
    // numbers becoming a public contract.
    [Fact]
    public void Serialized_EverySectionTypeOnTheWireIsANameNotANumber()
    {
        var analysis = BuildAnalysis(
            Advice(SectionType.Languages, RecommendationPriority.Critical,
                RecommendationKind.LanguageMissing, "Add English.", 0.10));

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(AnalysisResponse.From(analysis), WebOptions));

        var sections = json.RootElement.GetProperty("breakdown").GetProperty("sections");
        sections.EnumerateArray().Select(s => s.GetProperty("section").GetString()).Should().Equal(
            "Skills", "Experience", "Education", "Certifications", "Projects", "Languages");

        json.RootElement.GetProperty("recommendations")[0]
            .GetProperty("section").GetString().Should().Be("Languages");
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

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

        var response = AnalysisResponse.From(analysis, isStale: false);

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

        var response = AnalysisResponse.From(analysis, isStale: false);

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
            JsonSerializer.Serialize(AnalysisResponse.From(analysis, isStale: false), WebOptions));

        var recommendation = json.RootElement.GetProperty("recommendations")[0];
        recommendation.GetProperty("section").GetString().Should().Be("Skills");
        recommendation.GetProperty("priority").GetString().Should().Be("Critical");
        recommendation.GetProperty("kind").GetString().Should().Be("MissingMustHaveSkill");
        recommendation.GetProperty("impact").GetDouble().Should().Be(0.15);
    }

    // The two v1 contract settlements, pinned at the mapper: `band` carries the ScoreBand NAME like
    // every other enum in the response, and the three ids are bare guids rather than {"value": guid}
    // envelopes. Bare means bare: GetGuid() on the property itself would throw if an envelope object
    // ever came back, so the assertion cannot pass on a wrapped id. `recommendations` stays an array
    // of objects — it was string[] before the scoring chain and a typed client breaks on the first
    // non-empty array, which is why the shape is worth restating beside the fields that changed here.
    [Fact]
    public void Serialized_BandCarriesItsNameAndIdsAreBareGuids()
    {
        var analysis = BuildAnalysis(
            Advice(SectionType.Skills, RecommendationPriority.Critical,
                RecommendationKind.MissingMustHaveSkill, "Add SQL.", 0.15));

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(AnalysisResponse.From(analysis, isStale: false), WebOptions));
        var root = json.RootElement;

        root.GetProperty("band").GetString().Should().Be(analysis.Band.ToString(),
            "every enum on the v1 wire carries its name — band was the one integer left");
        root.GetProperty("id").GetGuid().Should().Be(analysis.Id.Value);
        root.GetProperty("resumeId").GetGuid().Should().Be(analysis.ResumeId.Value);
        root.GetProperty("jobPostingId").GetGuid().Should().Be(analysis.JobPostingId.Value);

        root.GetProperty("recommendations")[0].ValueKind.Should().Be(JsonValueKind.Object);
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
            JsonSerializer.Serialize(AnalysisResponse.From(analysis, isStale: false), WebOptions));

        var sections = json.RootElement.GetProperty("breakdown").GetProperty("sections");
        sections.EnumerateArray().Select(s => s.GetProperty("section").GetString()).Should().Equal(
            "Skills", "Experience", "Education", "Certifications", "Projects", "Languages");

        json.RootElement.GetProperty("recommendations")[0]
            .GetProperty("section").GetString().Should().Be("Languages");
    }

    // The full property list, in order, at both levels. A field silently disappearing is the failure a
    // per-field assertion cannot see; a field silently APPEARING is the other half, and this assertion is
    // what made adding `isStale` a decision rather than an accident.
    //
    // `isStale` is last on purpose. It is not part of the stored analysis — it is a comparison against the
    // resume as it stands right now — so appending it keeps the fields that describe the row together and
    // leaves the pre-existing order untouched.
    [Fact]
    public void Serialized_CarriesExactlyTheDocumentedFields()
    {
        var analysis = BuildAnalysis();

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(AnalysisResponse.From(analysis, isStale: false), WebOptions));
        var root = json.RootElement;

        NamesOf(root).Should().Equal(
            "id", "breakdown", "resumeId", "jobPostingId", "scoredAt", "recommendations",
            "overallScore", "band", "isStale");

        NamesOf(root.GetProperty("breakdown")).Should().Equal(
            "skillsScore", "experienceScore", "educationScore", "certificationsScore", "projectsScore",
            "languagesScore", "weights", "weightedTotal", "sections");

        NamesOf(root.GetProperty("breakdown").GetProperty("weights")).Should().Equal(
            "skills", "experience", "education", "certifications", "projects", "languages",
            "schemaVersion");

        NamesOf(root.GetProperty("breakdown").GetProperty("sections")[0]).Should().Equal(
            "section", "score", "weight");
    }

    // The flag is passed straight through, both ways round. It is not derived here and must not be:
    // deciding staleness needs the CURRENT resume, which this DTO never sees, so a mapper that computed
    // anything of its own would be computing it from the wrong inputs.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Serialized_CarriesTheStalenessItWasGiven(bool isStale)
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(AnalysisResponse.From(BuildAnalysis(), isStale), WebOptions));

        json.RootElement.GetProperty("isStale").GetBoolean().Should().Be(isStale);
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

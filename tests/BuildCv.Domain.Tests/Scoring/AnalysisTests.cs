using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Scoring;

public class AnalysisTests
{
    private static readonly ScoringWeightsSnapshot DefaultWeights = ScoringWeightsSnapshot.Default();

    [Fact]
    public void Analysis_with_low_band_can_be_created()
    {
        var breakdown = ScoreBreakdown.Create(0.3, 0.4, 0.2, 0.5, 0.8, 0.25, DefaultWeights);
        var analysis = Analysis.Create(
            id: AnalysisId.New(),
            breakdown: breakdown,
            resumeId: ResumeId.New(),
            jobPostingId: JobPostingId.New(),
            scoredAt: DateTimeOffset.Now,
            recommendations: [Advice("Add more skills"), Advice("Improve summary")]);

        // 0.45*0.3 + 0.20*0.4 + 0.10*0.2 + 0.10*0.5 + 0.05*0.8 + 0.10*0.25, under the six-section
        // weights. It was 34 while Education carried 0.20 and there was no Languages term.
        analysis.OverallScore.Should().Be(35);
        analysis.Band.Should().Be(ScoreBand.Low);
        analysis.Recommendations.Should().HaveCount(2);
    }

    [Fact]
    public void Analysis_with_defaults_can_be_created()
    {
        var breakdown = ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights);
        var analysis = Analysis.Create(
            id: AnalysisId.New(),
            breakdown: breakdown,
            resumeId: ResumeId.New(),
            jobPostingId: JobPostingId.New(),
            scoredAt: DateTimeOffset.Now);

        analysis.Recommendations.Should().BeEmpty();
    }

    // Every section scores the same, so the weights sum to one and the total IS that score — the band
    // boundaries are being tested, not the arithmetic above them.
    [Fact]
    public void Analysis_band_thresholds()
    {
        var medium = BuildAnalysis(ScoreBreakdown.Create(0.45, 0.45, 0.45, 0.45, 0.45, 0.45, DefaultWeights));
        var good = BuildAnalysis(ScoreBreakdown.Create(0.65, 0.65, 0.65, 0.65, 0.65, 0.65, DefaultWeights));
        var strong = BuildAnalysis(ScoreBreakdown.Create(0.9, 0.9, 0.9, 0.9, 0.9, 0.9, DefaultWeights));

        medium.Band.Should().Be(ScoreBand.Medium);
        good.Band.Should().Be(ScoreBand.Good);
        strong.Band.Should().Be(ScoreBand.Strong);
    }

    private static Analysis BuildAnalysis(ScoreBreakdown breakdown) =>
        Analysis.Create(
            id: AnalysisId.New(),
            breakdown: breakdown,
            resumeId: ResumeId.New(),
            jobPostingId: JobPostingId.New(),
            scoredAt: DateTimeOffset.Now);

    [Fact]
    public void Recommendations_are_set_at_creation()
    {
        var advice = Advice("Add more skills");
        var analysis = Analysis.Create(
            id: AnalysisId.New(),
            breakdown: ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights),
            resumeId: ResumeId.New(),
            jobPostingId: JobPostingId.New(),
            scoredAt: DateTimeOffset.Now,
            recommendations: [advice]);

        analysis.Recommendations.Should().ContainSingle().Which.Should().Be(advice);
    }

    // The structure travels with the sentence. It is what the analytics group by, and it is the half
    // that survives the sentence being encrypted at rest.
    [Fact]
    public void Recommendations_keep_their_section_priority_and_kind()
    {
        var analysis = Analysis.Create(
            id: AnalysisId.New(),
            breakdown: ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights),
            resumeId: ResumeId.New(),
            jobPostingId: JobPostingId.New(),
            scoredAt: DateTimeOffset.Now,
            recommendations:
            [
                Recommendation.Create(
                    SectionType.Languages,
                    RecommendationPriority.Critical,
                    RecommendationKind.LanguageMissing,
                    "This role requires English.",
                    0.10),
            ]);

        var recommendation = analysis.Recommendations.Should().ContainSingle().Subject;
        recommendation.Section.Should().Be(SectionType.Languages);
        recommendation.Priority.Should().Be(RecommendationPriority.Critical);
        recommendation.Kind.Should().Be(RecommendationKind.LanguageMissing);
        recommendation.Impact.Should().Be(0.10);
    }

    [Fact]
    public void Analysis_null_id_throws()
    {
        var act = () => Analysis.Create(
            id: null!,
            breakdown: ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights),
            resumeId: ResumeId.New(),
            jobPostingId: JobPostingId.New(),
            scoredAt: DateTimeOffset.Now);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Analysis_null_breakdown_throws()
    {
        var act = () => Analysis.Create(
            id: AnalysisId.New(),
            breakdown: null!,
            resumeId: ResumeId.New(),
            jobPostingId: JobPostingId.New(),
            scoredAt: DateTimeOffset.Now);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Analysis_null_resume_id_throws()
    {
        var act = () => Analysis.Create(
            id: AnalysisId.New(),
            breakdown: ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights),
            resumeId: null!,
            jobPostingId: JobPostingId.New(),
            scoredAt: DateTimeOffset.Now);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Analysis_null_job_posting_id_throws()
    {
        var act = () => Analysis.Create(
            id: AnalysisId.New(),
            breakdown: ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights),
            resumeId: ResumeId.New(),
            jobPostingId: null!,
            scoredAt: DateTimeOffset.Now);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Analysis_recommendations_are_defensively_copied()
    {
        var source = new List<Recommendation> { Advice("Add more skills") };
        var analysis = Analysis.Create(
            id: AnalysisId.New(),
            breakdown: ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights),
            resumeId: ResumeId.New(),
            jobPostingId: JobPostingId.New(),
            scoredAt: DateTimeOffset.Now,
            recommendations: source);

        source.Add(Advice("Improve summary"));

        analysis.Recommendations.Should().HaveCount(1);
    }

    [Fact]
    public void Analysis_equality_by_id()
    {
        var breakdown = ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, DefaultWeights);
        var id = AnalysisId.New();
        var a1 = Analysis.Create(id, breakdown, ResumeId.New(), JobPostingId.New(), DateTimeOffset.Now);
        var a2 = Analysis.Create(id, breakdown, ResumeId.New(), JobPostingId.New(), DateTimeOffset.Now);
        var a3 = Analysis.Create(AnalysisId.New(), breakdown, ResumeId.New(), JobPostingId.New(), DateTimeOffset.Now);

        a1.Should().Be(a2);
        a1.Should().NotBe(a3);
    }

    private static Recommendation Advice(string message) =>
        Recommendation.Create(
            SectionType.Skills, RecommendationPriority.Important, RecommendationKind.MissingMustHaveSkill, message, 0.2);
}

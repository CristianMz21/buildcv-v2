using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AnalysisRepositoryTests
{
    private readonly SqlServerFixture _fixture;

    public AnalysisRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AddAsync_ThenGetByResumeIdAsync_RoundTripsTheBreakdownAndRecommendations()
    {
        var resumeId = ResumeId.New();
        var analysis = NewAnalysis(resumeId, 0.9);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Analyses(writer).AddAsync(analysis);

        await using var reader = _fixture.NewApplicationContext();
        var found = await TestRepositories.Analyses(reader).GetByResumeIdAsync(resumeId);

        var reloaded = found.Should().ContainSingle().Subject;
        reloaded.Id.Should().Be(analysis.Id);
        reloaded.Breakdown.Should().Be(analysis.Breakdown);
        reloaded.Recommendations.Should().Equal(analysis.Recommendations);
        reloaded.OverallScore.Should().Be(analysis.OverallScore);
        reloaded.JobPostingId.Should().Be(analysis.JobPostingId);
    }

    // Score history, oldest first. Ordering on Seq rather than ScoredAt because ScoredAt is supplied by
    // the caller and two analyses of the same resume can carry the same instant — here they deliberately
    // do, so a ScoredAt ordering would be free to return them either way round.
    [Fact]
    public async Task GetByResumeIdAsync_ReturnsThatResumesHistoryInInsertOrder()
    {
        var resumeId = ResumeId.New();
        var scoredAt = DateTimeOffset.UtcNow;
        var first = NewAnalysis(resumeId, 0.4, scoredAt);
        var second = NewAnalysis(resumeId, 0.7, scoredAt);

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Analyses(writer);
            await repository.AddAsync(first);
            await repository.AddAsync(second);
            await repository.AddAsync(NewAnalysis(ResumeId.New(), 0.5));
        }

        await using var reader = _fixture.NewApplicationContext();
        var history = await TestRepositories.Analyses(reader).GetByResumeIdAsync(resumeId);

        history.Select(analysis => analysis.Id).Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task GetByResumeIdAsync_ForAResumeThatWasNeverScored_IsEmpty()
    {
        await using var reader = _fixture.NewApplicationContext();

        (await TestRepositories.Analyses(reader).GetByResumeIdAsync(ResumeId.New())).Should().BeEmpty();
    }

    private static Analysis NewAnalysis(ResumeId resumeId, double skills, DateTimeOffset? scoredAt = null) =>
        Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(skills, 0.8, 0.7, 0.6, 0.5, ScoringWeightsSnapshot.Default()),
            resumeId,
            JobPostingId.New(),
            scoredAt ?? DateTimeOffset.UtcNow,
            ["Add more C# projects.", "Mention SQL explicitly."]);
}

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
    public async Task AddAsync_ThenGetPageByResumeIdAsync_RoundTripsTheBreakdownAndRecommendations()
    {
        var resumeId = ResumeId.New();
        var analysis = NewAnalysis(resumeId, 0.9);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Analyses(writer).AddAsync(analysis);

        await using var reader = _fixture.NewApplicationContext();
        var found = await TestRepositories.Analyses(reader).GetPageByResumeIdAsync(resumeId, PageRequests.Of());

        var reloaded = found.Items.Should().ContainSingle().Subject;
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
    public async Task GetPageByResumeIdAsync_ReturnsThatResumesHistoryInInsertOrder()
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
        var history = await TestRepositories.Analyses(reader).GetPageByResumeIdAsync(resumeId, PageRequests.Of());

        history.Items.Select(analysis => analysis.Id).Should().Equal(first.Id, second.Id);
    }

    // Forwards, not backwards, and the cursor boundary flips with it: page two must start AFTER the row
    // page one ended on, so a `<` copied from the newest-first path would return the same row twice and
    // then run out.
    [Fact]
    public async Task GetPageByResumeIdAsync_WalkedByCursor_ReplaysTheHistoryForwardsExactlyOnce()
    {
        var resumeId = ResumeId.New();
        var scoredAt = DateTimeOffset.UtcNow;
        var history = new List<Analysis>();

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Analyses(writer);
            for (var index = 0; index < 5; index++)
            {
                var analysis = NewAnalysis(resumeId, 0.1 * (index + 1), scoredAt);
                await repository.AddAsync(analysis);
                history.Add(analysis);
            }

            await repository.AddAsync(NewAnalysis(ResumeId.New(), 0.5));
        }

        await using var reader = _fixture.NewApplicationContext();
        var repositoryForReads = TestRepositories.Analyses(reader);

        var visited = new List<AnalysisId>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await repositoryForReads.GetPageByResumeIdAsync(resumeId, PageRequests.Of(2, cursor));
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items.Select(analysis => analysis.Id));
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 2, 1);
        visited.Should().Equal(history.Select(analysis => analysis.Id));
    }

    [Fact]
    public async Task GetPageByResumeIdAsync_ForAResumeThatWasNeverScored_IsAnEmptyFinalPage()
    {
        await using var reader = _fixture.NewApplicationContext();

        var page = await TestRepositories.Analyses(reader).GetPageByResumeIdAsync(ResumeId.New(), PageRequests.Of());

        page.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    private static Analysis NewAnalysis(ResumeId resumeId, double skills, DateTimeOffset? scoredAt = null) =>
        Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(skills, 0.8, 0.7, 0.6, 0.5, 0.4, ScoringWeightsSnapshot.Default()),
            resumeId,
            JobPostingId.New(),
            scoredAt ?? DateTimeOffset.UtcNow,
            [
                Recommendation.Create(
                    SectionType.Projects, RecommendationPriority.Important,
                    RecommendationKind.FewerProjectsThanExpected, "Add more C# projects.", 0.05),
                Recommendation.Create(
                    SectionType.Skills, RecommendationPriority.Critical,
                    RecommendationKind.MissingMustHaveSkill, "Mention SQL explicitly.", 0.45),
            ]);
}

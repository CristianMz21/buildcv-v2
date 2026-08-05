using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

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
    public async Task GetByIdAsync_AfterAdd_RoundTripsTheBreakdownAndRecommendations()
    {
        var analysis = NewAnalysis(ResumeId.New(), 0.9);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Analyses(writer).AddAsync(analysis);

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Analyses(reader).GetByIdAsync(analysis.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Breakdown.Should().Be(analysis.Breakdown);
        reloaded.ResumeId.Should().Be(analysis.ResumeId);
        reloaded.Recommendations.Should().BeEquivalentTo(analysis.Recommendations);
    }

    // PROVENANCE ROUND-TRIPS AT FULL PRECISION, which is the property the de-duplication rests on rather
    // than a mapping detail. That rule compares this column for EQUALITY against Resume.UpdatedAt loaded
    // from a different table, so the two only ever agree if neither side loses precision: both are
    // `datetimeoffset`, whose default scale is 7 — one .NET tick. A column mapped a scale short would
    // truncate here, every comparison would miss, and the only symptom would be a de-duplication that
    // never fires and a score permanently reported as stale. Nothing about a score's VALUE would change,
    // so no other test in this suite could see it.
    //
    // The sub-millisecond ticks are what make that assertion able to fail; a whole-second seed would
    // survive any scale down to 0.
    //
    // The three timestamps are DISTINCT from each other on purpose. Three columns of one type in one row
    // is exactly the shape where a crossed mapping costs nothing at write time, and reusing one value
    // would hide it.
    [Fact]
    public async Task AddAsync_ThenGetByIdAsync_RoundTripsTheProvenanceItScoredAtFullPrecision()
    {
        var resumeUpdatedAt = new DateTimeOffset(2026, 7, 1, 9, 15, 30, TimeSpan.Zero).AddTicks(1234567);
        var jobPostingUpdatedAt = new DateTimeOffset(2026, 7, 2, 18, 45, 0, TimeSpan.Zero).AddTicks(7654321);
        var scoredAt = new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero).AddTicks(9999999);

        var analysis = Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.5, 0.4, ScoringWeightsSnapshot.Default()),
            ResumeId.New(),
            JobPostingId.New(),
            scoredAt,
            recommendations: null,
            resumeUpdatedAt: resumeUpdatedAt,
            jobPostingUpdatedAt: jobPostingUpdatedAt);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Analyses(writer).AddAsync(analysis);

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Analyses(reader).GetByIdAsync(analysis.Id);

        reloaded.Should().NotBeNull();
        reloaded!.ResumeUpdatedAt.Should().Be(resumeUpdatedAt);
        reloaded.JobPostingUpdatedAt.Should().Be(jobPostingUpdatedAt);
        reloaded.ScoredAt.Should().Be(scoredAt);

        // The comparison the product actually performs, executed rather than reasoned about: an instant
        // that made the round trip must still be EQUAL to the in-memory one it came from.
        reloaded.IsStaleFor(resumeUpdatedAt).Should().BeFalse(
            "a resume untouched since the score was taken is not stale");
    }

    // WHAT A HISTORICAL ROW LOOKS LIKE. The migration adds both columns as nullable, so every analysis
    // written before it reads back with neither, and this is that row: Analysis.Create defaults both to
    // null exactly so a writer that cannot know what it scored says so.
    //
    // Null must read as STALE, never as fresh. It is the unsafe direction that is cheap to get wrong —
    // `ResumeUpdatedAt == null || ResumeUpdatedAt != current` and `ResumeUpdatedAt != current` behave
    // identically here, but a nullable comparison written the other way round ("no provenance recorded,
    // so nothing has changed") would tell a candidate a score taken against a CV nobody can identify is
    // current.
    [Fact]
    public async Task AddAsync_WithoutProvenance_ReadsBackAsUnknownAndThereforeStale()
    {
        var analysis = NewAnalysis(ResumeId.New(), 0.5);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Analyses(writer).AddAsync(analysis);

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Analyses(reader).GetByIdAsync(analysis.Id);

        reloaded.Should().NotBeNull();
        reloaded!.ResumeUpdatedAt.Should().BeNull();
        reloaded.JobPostingUpdatedAt.Should().BeNull();
        reloaded.IsStaleFor(DateTimeOffset.UtcNow).Should().BeTrue(
            "a score that cannot say which version of the resume it scored is not known to be current");
    }

    [Fact]
    public async Task GetByIdAsync_ForAnIdThatWasNeverStored_ReturnsNull()
    {
        await using var reader = _fixture.NewApplicationContext();

        (await TestRepositories.Analyses(reader).GetByIdAsync(AnalysisId.New())).Should().BeNull();
    }

    // THE SOFT DELETE, reached the only way it can be reached: Analysis has no Delete() of its own, and
    // the sole writer of its DeletedAt column is the cascade in ResumeRepository.DeleteAsync. Nothing in
    // GetByIdAsync mentions the tombstone — the global query filter does — so this is the test that says
    // the filter is on this path and not only on the paged one.
    //
    // Reading it back with IgnoreQueryFilters is what separates "filtered out" from "never written" or
    // "hard deleted": without that line a cascade that DESTROYED the row would pass this test, and the
    // privacy decision recorded in CascadeToAnalysesAsync is explicitly a tombstone, not a DELETE.
    [Fact]
    public async Task GetByIdAsync_AfterTheResumeItScoredWasDeleted_ReturnsNullButKeepsTheRow()
    {
        var resume = Resume.Create(AccountId.New(), new ContactInformation(
            PersonName.Create("Test Person"), Email.Create($"scored.{Guid.NewGuid():N}@example.com")));
        var analysis = NewAnalysis(resume.Id, 0.6);

        await using (var writer = _fixture.NewApplicationContext())
        {
            await TestRepositories.Resumes(writer).AddAsync(resume);
            await TestRepositories.Analyses(writer).AddAsync(analysis);
        }

        await using (var deleter = _fixture.NewApplicationContext())
            await TestRepositories.Resumes(deleter).DeleteAsync(resume.Id);

        await using var reader = _fixture.NewApplicationContext();

        (await TestRepositories.Analyses(reader).GetByIdAsync(analysis.Id)).Should().BeNull(
            "deleting a resume hides every score derived from it");
        (await reader.Analyses.IgnoreQueryFilters().SingleAsync(entity => entity.Id == analysis.Id))
            .Breakdown.Should().Be(analysis.Breakdown, "the history is tombstoned for audit, not destroyed");
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

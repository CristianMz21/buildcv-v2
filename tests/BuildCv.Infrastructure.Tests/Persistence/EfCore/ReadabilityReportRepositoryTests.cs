using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// The readability read path against a real SQL Server. It is the half InMemoryRepositoryTests cannot
// certify — the ordering there is an insertion counter standing in for a bigint IDENTITY, and whether
// the cursor comparison TRANSLATES at all is a question only a database can answer.
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReadabilityReportRepositoryTests
{
    private readonly SqlServerFixture _fixture;

    public ReadabilityReportRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    // The round trip that matters most, because the Message on every recommendation is AES-GCM sealed
    // under its own context string: it leaves as text, is stored as varbinary, and has to come back as
    // the same text through a second context that never saw the first one's state.
    [Fact]
    public async Task AddAsync_ThenGetByIdAsync_RoundTripsTheBreakdownAndItsAdvice()
    {
        var report = NewReport(ResumeId.New());

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.ReadabilityReports(writer).AddAsync(report);

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.ReadabilityReports(reader).GetByIdAsync(report.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Breakdown.Should().Be(report.Breakdown);
        reloaded.ResumeId.Should().Be(report.ResumeId);
        reloaded.Recommendations.Should().Equal(report.Recommendations);
        reloaded.ReadabilityScore.Should().Be(report.ReadabilityScore);
    }

    [Fact]
    public async Task GetByIdAsync_ForAnIdThatWasNeverStored_IsNull()
    {
        await using var reader = _fixture.NewApplicationContext();

        (await TestRepositories.ReadabilityReports(reader).GetByIdAsync(ReadabilityReportId.New()))
            .Should().BeNull();
    }

    // Readability history, oldest first. Ordered on Seq rather than EvaluatedAt because EvaluatedAt
    // comes from the handler's clock and two reports of one resume can carry the same instant — here
    // they deliberately do, so an EvaluatedAt ordering would be free to return them either way round.
    //
    // The third report belongs to another resume and must never appear: the Where is what the
    // (ResumeId, Seq) index exists to serve.
    [Fact]
    public async Task GetPageByResumeIdAsync_ReturnsThatResumesHistoryInInsertOrder()
    {
        var resumeId = ResumeId.New();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var first = NewReport(resumeId, 0.4, evaluatedAt);
        var second = NewReport(resumeId, 0.7, evaluatedAt);

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.ReadabilityReports(writer);
            await repository.AddAsync(first);
            await repository.AddAsync(second);
            await repository.AddAsync(NewReport(ResumeId.New()));
        }

        await using var reader = _fixture.NewApplicationContext();
        var history = await TestRepositories.ReadabilityReports(reader)
            .GetPageByResumeIdAsync(resumeId, PageRequests.Of());

        history.Items.Select(report => report.Id).Should().Equal(first.Id, second.Id);
    }

    // Forwards, not backwards, and the cursor boundary flips with it: page two must start AFTER the row
    // page one ended on, so a `<` copied from the newest-first path would return the same row twice and
    // then run out.
    [Fact]
    public async Task GetPageByResumeIdAsync_WalkedByCursor_ReplaysTheHistoryForwardsExactlyOnce()
    {
        var resumeId = ResumeId.New();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var history = new List<ReadabilityReport>();

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.ReadabilityReports(writer);
            for (var index = 0; index < 5; index++)
            {
                var report = NewReport(resumeId, 0.1 * (index + 1), evaluatedAt);
                await repository.AddAsync(report);
                history.Add(report);
            }

            await repository.AddAsync(NewReport(ResumeId.New()));
        }

        await using var reader = _fixture.NewApplicationContext();
        var repositoryForReads = TestRepositories.ReadabilityReports(reader);

        var visited = new List<ReadabilityReportId>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await repositoryForReads.GetPageByResumeIdAsync(resumeId, PageRequests.Of(2, cursor));
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items.Select(report => report.Id));
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 2, 1);
        visited.Should().Equal(history.Select(report => report.Id));
    }

    // The advice survives the PAGED path too, not only the by-id one — which is exactly what
    // AsSplitQuery could break without any page-shape assertion noticing. The probe projects the entity
    // alongside its Seq and then splits, and whether an owned collection still travels through that
    // projection is a question to answer rather than assume.
    [Fact]
    public async Task GetPageByResumeIdAsync_CarriesEachReportsRecommendations()
    {
        var resumeId = ResumeId.New();
        var report = NewReport(resumeId);

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.ReadabilityReports(writer).AddAsync(report);

        await using var reader = _fixture.NewApplicationContext();
        var page = await TestRepositories.ReadabilityReports(reader)
            .GetPageByResumeIdAsync(resumeId, PageRequests.Of());

        page.Items.Should().ContainSingle().Which.Recommendations.Should().Equal(report.Recommendations);
    }

    private static ReadabilityReport NewReport(
        ResumeId resumeId, double completeness = 0.9, DateTimeOffset? evaluatedAt = null) =>
        ReadabilityReport.Create(
            ReadabilityReportId.New(),
            ReadabilityBreakdown.Create(completeness, 0.8, 0.7, 0.6, 0.0, ReadabilityWeightsSnapshot.Default()),
            resumeId,
            evaluatedAt ?? DateTimeOffset.UtcNow,
            [
                ReadabilityRecommendation.Create(
                    ReadabilitySectionType.Contact, RecommendationPriority.Important,
                    ReadabilityRecommendationKind.NoPhoneNumber, "Add a phone number.", 0.05),
            ]);
}

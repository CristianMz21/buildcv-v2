using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Security;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence;

public class InMemoryRepositoryTests
{
    private static Account CreateAccount(string email = "user@example.com") =>
        Account.Create(Email.Create(email), Password.Create(new PasswordHasher().Hash("password")));

    private static Resume CreateResume(AccountId ownerId) =>
        Resume.Create(ownerId, new ContactInformation(PersonName.Create("Jane Doe"), Email.Create("jane@example.com")));

    private static Analysis NewAnalysis(ResumeId resumeId) =>
        Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, ScoringWeightsSnapshot.Default()),
            resumeId,
            JobPostingId.New(),
            DateTimeOffset.UtcNow);

    private static ReadabilityReport NewReadabilityReport(ResumeId resumeId) =>
        ReadabilityReport.Create(
            ReadabilityReportId.New(),
            ReadabilityBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.0, ReadabilityWeightsSnapshot.Default()),
            resumeId,
            DateTimeOffset.UtcNow,
            [
                ReadabilityRecommendation.Create(
                    ReadabilitySectionType.Contact, RecommendationPriority.Important,
                    ReadabilityRecommendationKind.NoPhoneNumber, "Add a phone number.", 0.05),
            ]);

    [Fact]
    public async Task Account_add_and_get_by_id_roundtrip()
    {
        var repository = new InMemoryAccountRepository();
        var account = CreateAccount();

        await repository.AddAsync(account);
        var found = await repository.GetByIdAsync(account.Id);

        found.Should().Be(account);
    }

    [Fact]
    public async Task Account_get_by_email_is_case_insensitive()
    {
        var repository = new InMemoryAccountRepository();
        var account = CreateAccount();
        await repository.AddAsync(account);

        var found = await repository.GetByEmailAsync(Email.Create("USER@example.com"));
        var exists = await repository.ExistsByEmailAsync(Email.Create("User@Example.COM"));

        found.Should().Be(account);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task Account_get_by_id_unknown_returns_null()
    {
        var repository = new InMemoryAccountRepository();

        (await repository.GetByIdAsync(AccountId.New())).Should().BeNull();
    }

    // The in-memory store has to answer the port the way SQL Server does, or an Api test written against
    // it certifies behavior that does not exist in production. Under EF a domain delete writes the
    // DeletedAt tombstone alongside the status, so the account disappears from every lookup and the
    // filtered unique index releases its address; a dictionary has neither, so the equivalence is stated
    // in the repository and checked here.
    [Fact]
    public async Task Account_domain_delete_hides_it_and_frees_the_address()
    {
        var repository = new InMemoryAccountRepository();
        var account = CreateAccount();
        await repository.AddAsync(account);

        account.Delete();
        await repository.UpdateAsync(account);

        (await repository.GetByIdAsync(account.Id)).Should().BeNull();
        (await repository.GetByEmailAsync(account.Email)).Should().BeNull();
        (await repository.ExistsByEmailAsync(account.Email)).Should().BeFalse();

        var replacement = CreateAccount();
        await repository.AddAsync(replacement);

        (await repository.GetByEmailAsync(replacement.Email))!.Id.Should().Be(replacement.Id);
    }

    [Fact]
    public async Task Account_suspend_leaves_it_visible()
    {
        var repository = new InMemoryAccountRepository();
        var account = CreateAccount();
        await repository.AddAsync(account);

        account.Suspend();
        await repository.UpdateAsync(account);

        (await repository.GetByIdAsync(account.Id)).Should().Be(account,
            "only Deleted is a tombstone; a suspended account still has to be findable to be restored");
    }

    [Fact]
    public async Task Organization_domain_delete_hides_it_and_frees_the_slug()
    {
        var repository = new InMemoryOrganizationRepository();
        var slug = Slug.Create("contoso");
        var organization = Organization.Create(OrganizationName.Create("Contoso"), slug, AccountId.New());
        await repository.AddAsync(organization);

        organization.Delete();
        await repository.UpdateAsync(organization);

        (await repository.GetByIdAsync(organization.Id)).Should().BeNull();
        (await repository.GetBySlugAsync(slug)).Should().BeNull();
    }

    [Fact]
    public async Task RefreshToken_revoke_makes_get_by_token_return_null()
    {
        var repository = new InMemoryRefreshTokenRepository();
        var tokenValue = new string('a', 86);
        var createdAt = DateTimeOffset.UtcNow;
        var refreshToken = RefreshToken.Create(tokenValue, AccountId.New(), createdAt, createdAt.AddDays(30));
        await repository.AddAsync(refreshToken);

        (await repository.GetByTokenAsync(tokenValue)).Should().Be(refreshToken);

        await repository.RevokeAsync(tokenValue);

        (await repository.GetByTokenAsync(tokenValue)).Should().BeNull();
    }

    [Fact]
    public async Task RefreshToken_revoke_all_for_account_drops_only_that_accounts_tokens()
    {
        var repository = new InMemoryRefreshTokenRepository();
        var accountId = AccountId.New();
        var createdAt = DateTimeOffset.UtcNow;

        var first = RefreshToken.Create(new string('a', 86), accountId, createdAt, createdAt.AddDays(30));
        var second = RefreshToken.Create(new string('b', 86), accountId, createdAt, createdAt.AddDays(30));
        var bystander = RefreshToken.Create(new string('c', 86), AccountId.New(), createdAt, createdAt.AddDays(30));
        await repository.AddAsync(first);
        await repository.AddAsync(second);
        await repository.AddAsync(bystander);

        await repository.RevokeAllForAccountAsync(accountId);

        (await repository.GetByTokenAsync(first.Token)).Should().BeNull();
        (await repository.GetByTokenAsync(second.Token)).Should().BeNull();
        (await repository.GetByTokenAsync(bystander.Token)).Should().Be(bystander);
    }

    [Fact]
    public async Task Resume_add_and_get_by_id_roundtrip()
    {
        var repository = new InMemoryResumeRepository(
            new InMemoryAnalysisRepository(), new InMemoryReadabilityReportRepository());
        var ownerId = AccountId.New();
        var resume = CreateResume(ownerId);

        await repository.AddAsync(resume);
        var found = await repository.GetByIdAsync(resume.Id);
        var byOwner = await repository.GetPageByOwnerIdAsync(ownerId, PageRequests.Of());

        found.Should().Be(resume);
        byOwner.Items.Should().ContainSingle().Which.Should().Be(resume);
        byOwner.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Resume_delete_removes_it()
    {
        var repository = new InMemoryResumeRepository(
            new InMemoryAnalysisRepository(), new InMemoryReadabilityReportRepository());
        var resume = CreateResume(AccountId.New());
        await repository.AddAsync(resume);

        await repository.DeleteAsync(resume.Id);

        (await repository.GetByIdAsync(resume.Id)).Should().BeNull();
    }

    // THE PAIR OF ResumeRepositoryTests.DeleteAsync_AlsoTombstonesTheAnalysesDerivedFromTheResume, which
    // makes the same claim against a real SQL Server. Two providers, one promise: deleting a resume
    // takes its whole score history out of every read.
    //
    // It was true of EF and not of this store (issue #18), and the Api suite runs on this store — so a
    // handler that read an analysis without loading its resume first would have been certified green
    // against behaviour production does not have, on a PRIVACY promise. Nothing observed it only
    // because both endpoints that read an analysis authorize against the resume first.
    //
    // BOTH READ PORTS, not just one. GetPageByResumeIdAsync is where the orphans were listed and
    // GetByIdAsync inherits the same store, so a cascade that cleared one index and not the other would
    // pass a test that checked either alone.
    //
    // THE BYSTANDER IS THE POINT OF THE SECOND HALF. A DeleteAsync that emptied the analysis store
    // wholesale would satisfy every assertion above it, and would delete another candidate's score
    // history on every delete.
    [Fact]
    public async Task Resume_delete_also_removes_the_analyses_derived_from_it()
    {
        var analyses = new InMemoryAnalysisRepository();
        var repository = new InMemoryResumeRepository(analyses, new InMemoryReadabilityReportRepository());

        var resume = CreateResume(AccountId.New());
        await repository.AddAsync(resume);
        var derived = NewAnalysis(resume.Id);
        await analyses.AddAsync(derived);

        var survivor = CreateResume(AccountId.New());
        await repository.AddAsync(survivor);
        var bystander = NewAnalysis(survivor.Id);
        await analyses.AddAsync(bystander);

        await repository.DeleteAsync(resume.Id);

        (await analyses.GetByIdAsync(derived.Id)).Should().BeNull("the resume it was derived from is gone");
        (await analyses.GetPageByResumeIdAsync(resume.Id, PageRequests.Of())).Items
            .Should().BeEmpty("the history is not merely unreachable by id, it is gone from the list too");

        (await analyses.GetByIdAsync(bystander.Id)).Should().Be(bystander);
        (await analyses.GetPageByResumeIdAsync(survivor.Id, PageRequests.Of())).Items
            .Should().ContainSingle().Which.Should().Be(bystander);
    }

    [Fact]
    public async Task JobPosting_add_and_get_by_id_roundtrip()
    {
        var repository = new InMemoryJobPostingRepository();
        var ownerId = AccountId.New();
        var jobPosting = JobPosting.Create(ownerId, "Backend Developer", OrganizationName.Create("Acme"));

        await repository.AddAsync(jobPosting);
        var found = await repository.GetByIdAsync(jobPosting.Id);
        var byOwner = await repository.GetPageByOwnerIdAsync(ownerId, PageRequests.Of());

        found.Should().Be(jobPosting);
        byOwner.Items.Should().ContainSingle().Which.Should().Be(jobPosting);
    }

    [Fact]
    public async Task Organization_add_and_get_by_slug_is_case_insensitive()
    {
        var repository = new InMemoryOrganizationRepository();
        var organization = Organization.Create(
            OrganizationName.Create("Acme"), Slug.Create("acme-corp"), AccountId.New());

        await repository.AddAsync(organization);
        var found = await repository.GetBySlugAsync(Slug.Create("ACME-Corp"));

        found.Should().Be(organization);
        (await repository.GetByIdAsync(organization.Id)).Should().Be(organization);
    }

    [Fact]
    public async Task Analysis_get_by_resume_id_filters_by_resume()
    {
        var repository = new InMemoryAnalysisRepository();
        var resumeId = ResumeId.New();
        var breakdown = ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, ScoringWeightsSnapshot.Default());
        var matching = Analysis.Create(AnalysisId.New(), breakdown, resumeId, JobPostingId.New(), DateTimeOffset.UtcNow);
        var other = Analysis.Create(AnalysisId.New(), breakdown, ResumeId.New(), JobPostingId.New(), DateTimeOffset.UtcNow);
        await repository.AddAsync(matching);
        await repository.AddAsync(other);

        var found = await repository.GetPageByResumeIdAsync(resumeId, PageRequests.Of());

        found.Items.Should().ContainSingle().Which.Should().Be(matching);
    }

    // The de-duplication lookup, on the store the whole Api suite runs against. It must answer the row
    // ScoreResumeHandler would compare against — the NEWEST for that exact pair — so all three cases that
    // could wrongly match are present: an older row for the same pair, a row for the same resume against
    // a different posting, and a row for a different resume against the same posting.
    //
    // "Newest" is by insertion, matching AnalysisRepository's ordering on Seq, so every ScoredAt here is
    // deliberately the SAME instant: ordering on ScoredAt would be free to return either row and this
    // test would pass or fail by chance.
    [Fact]
    public async Task Analysis_get_latest_by_pair_returns_the_newest_row_for_that_pair_only()
    {
        var repository = new InMemoryAnalysisRepository();
        var resumeId = ResumeId.New();
        var jobPostingId = new JobPostingId(Guid.NewGuid());
        var scoredAt = DateTimeOffset.UtcNow;
        var breakdown = ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, ScoringWeightsSnapshot.Default());

        var older = Analysis.Create(AnalysisId.New(), breakdown, resumeId, jobPostingId, scoredAt);
        var newer = Analysis.Create(AnalysisId.New(), breakdown, resumeId, jobPostingId, scoredAt);
        var otherPosting = Analysis.Create(AnalysisId.New(), breakdown, resumeId, JobPostingId.New(), scoredAt);
        var otherResume = Analysis.Create(AnalysisId.New(), breakdown, ResumeId.New(), jobPostingId, scoredAt);

        await repository.AddAsync(older);
        await repository.AddAsync(otherPosting);
        await repository.AddAsync(otherResume);
        await repository.AddAsync(newer);

        (await repository.GetLatestByPairAsync(resumeId, jobPostingId)).Should().Be(newer);
        (await repository.GetLatestByPairAsync(ResumeId.New(), jobPostingId)).Should().BeNull(
            "a pair that was never scored has no latest row");
    }

    // The parity that makes every Api test meaningful. Api tests run against this store, so if it
    // answers the list ports in dictionary order — or hands out a cursor that skips a row — those tests
    // certify page behavior SQL Server has never produced. Same walk as
    // ResumeKeysetPaginationTests runs against a real database.
    [Fact]
    public async Task Resume_pages_walk_newest_first_without_a_gap_or_a_repeat()
    {
        var repository = new InMemoryResumeRepository(
            new InMemoryAnalysisRepository(), new InMemoryReadabilityReportRepository());
        var ownerId = AccountId.New();
        var mine = new List<Resume>();
        for (var index = 0; index < 5; index++)
        {
            var resume = CreateResume(ownerId);
            await repository.AddAsync(resume);
            mine.Add(resume);
            await repository.AddAsync(CreateResume(AccountId.New()));
        }

        var visited = new List<ResumeId>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await repository.GetPageByOwnerIdAsync(ownerId, PageRequests.Of(2, cursor));
            pageSizes.Add(page.Items.Count);
            visited.AddRange(page.Items.Select(resume => resume.Id));
            pageSizes.Count.Should().BeLessThan(20, "a cursor walk that never terminates is a bug, not a hang");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 2, 1);
        mine.Reverse();
        visited.Should().Equal(mine.Select(resume => resume.Id));
    }

    // An UPDATE does not move a row in the clustered index, so it must not move one here either —
    // otherwise editing an old resume would teleport it to the top of the list, past a cursor a client
    // was already walking.
    [Fact]
    public async Task Resume_update_does_not_move_the_resume_in_the_page_order()
    {
        var repository = new InMemoryResumeRepository(
            new InMemoryAnalysisRepository(), new InMemoryReadabilityReportRepository());
        var ownerId = AccountId.New();
        var first = CreateResume(ownerId);
        var second = CreateResume(ownerId);
        await repository.AddAsync(first);
        await repository.AddAsync(second);

        first.UpdateContactInformation(new ContactInformation(
            PersonName.Create("Renamed Person"), Email.Create("renamed@example.com")));
        await repository.UpdateAsync(first);

        var page = await repository.GetPageByOwnerIdAsync(ownerId, PageRequests.Of());

        page.Items.Select(resume => resume.Id).Should().Equal(second.Id, first.Id);
    }

    [Fact]
    public async Task Analysis_add_and_get_by_id_roundtrip()
    {
        var repository = new InMemoryAnalysisRepository();
        var analysis = NewAnalysis(ResumeId.New());
        await repository.AddAsync(analysis);

        (await repository.GetByIdAsync(analysis.Id)).Should().Be(analysis);
    }

    // The EF twin answers null for an id that was never stored AND for one whose row is tombstoned, and
    // both arrive at the same caller as the same nothing.
    //
    // This store reaches the second case by REMOVING the row rather than hiding it — see the cascade
    // test below — because an Analysis has no Delete() and no Status for an IsLive filter to read. The
    // observable is the same null either way, which is the only thing a caller can tell apart.
    [Fact]
    public async Task Analysis_get_by_id_unknown_returns_null()
    {
        var repository = new InMemoryAnalysisRepository();
        await repository.AddAsync(NewAnalysis(ResumeId.New()));

        (await repository.GetByIdAsync(AnalysisId.New())).Should().BeNull();
    }

    // Score history reads forwards, unlike every other paged list, so the in-memory store has to flip
    // the boundary comparison with it.
    [Fact]
    public async Task Analysis_pages_walk_oldest_first()
    {
        var repository = new InMemoryAnalysisRepository();
        var resumeId = ResumeId.New();
        var breakdown = ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, ScoringWeightsSnapshot.Default());
        var history = new List<AnalysisId>();
        for (var index = 0; index < 3; index++)
        {
            var analysis = Analysis.Create(
                AnalysisId.New(), breakdown, resumeId, JobPostingId.New(), DateTimeOffset.UtcNow);
            await repository.AddAsync(analysis);
            history.Add(analysis.Id);
        }

        var firstPage = await repository.GetPageByResumeIdAsync(resumeId, PageRequests.Of(2));
        var secondPage = await repository.GetPageByResumeIdAsync(
            resumeId, PageRequests.Of(2, firstPage.NextCursor));

        firstPage.Items.Select(analysis => analysis.Id).Should().Equal(history[0], history[1]);
        secondPage.Items.Select(analysis => analysis.Id).Should().Equal(history[2]);
        secondPage.NextCursor.Should().BeNull();
    }

    // The same direction, on the second aggregate keyed by ResumeId, and asserted here rather than left
    // to the handler tests: the whole Api suite runs on this store, so a store that answered newest
    // first would certify a history production replays the other way round.
    //
    // TWO PAGES, not one. A single page of three would come back in the same ORDER either way — the
    // boundary comparison is what flips with the direction, and it is only exercised once a cursor is
    // carried. Reversing this store's `>` to `<` leaves page one identical and empties page two.
    [Fact]
    public async Task ReadabilityReport_pages_walk_oldest_first()
    {
        var repository = new InMemoryReadabilityReportRepository();
        var resumeId = ResumeId.New();
        var history = new List<ReadabilityReportId>();
        for (var index = 0; index < 3; index++)
        {
            var report = NewReadabilityReport(resumeId);
            await repository.AddAsync(report);
            history.Add(report.Id);
        }

        var firstPage = await repository.GetPageByResumeIdAsync(resumeId, PageRequests.Of(2));
        var secondPage = await repository.GetPageByResumeIdAsync(
            resumeId, PageRequests.Of(2, firstPage.NextCursor));

        firstPage.Items.Select(report => report.Id).Should().Equal(history[0], history[1]);
        secondPage.Items.Select(report => report.Id).Should().Equal(history[2]);
        secondPage.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task ReadabilityReport_get_by_resume_id_filters_by_resume()
    {
        var repository = new InMemoryReadabilityReportRepository();
        var resumeId = ResumeId.New();
        var mine = NewReadabilityReport(resumeId);
        var somebodyElses = NewReadabilityReport(ResumeId.New());

        await repository.AddAsync(mine);
        await repository.AddAsync(somebodyElses);

        var page = await repository.GetPageByResumeIdAsync(resumeId, PageRequests.Of());

        page.Items.Should().ContainSingle().Which.Should().Be(mine);
        (await repository.GetByIdAsync(somebodyElses.Id)).Should().Be(somebodyElses,
            "filtering the list must not have made the other resume's report unreadable by id");
    }

    // THE PARITY THAT ONLY BECAME OBSERVABLE WHEN THE PORT GREW A READ.
    // ResumeRepositoryTests.DeleteAsync_AlsoTombstonesTheReadabilityReportsDerivedFromTheResume makes
    // the same claim against a real SQL Server, and until now it had to read the table through
    // IgnoreQueryFilters because no port method could ask. This store dropped nothing at all, and the
    // Api suite runs on it — so the first handler to read a report without loading its resume first
    // would have been certified green against behaviour production does not have, on a promise that
    // matters more here than for scoring: a readability recommendation quotes the candidate's own
    // bullet points and job titles.
    //
    // BOTH READ PORTS and a BYSTANDER, for the reasons written on the analysis test above.
    [Fact]
    public async Task Resume_delete_also_removes_the_readability_reports_derived_from_it()
    {
        var reports = new InMemoryReadabilityReportRepository();
        var repository = new InMemoryResumeRepository(new InMemoryAnalysisRepository(), reports);

        var resume = CreateResume(AccountId.New());
        await repository.AddAsync(resume);
        var derived = NewReadabilityReport(resume.Id);
        await reports.AddAsync(derived);

        var survivor = CreateResume(AccountId.New());
        await repository.AddAsync(survivor);
        var bystander = NewReadabilityReport(survivor.Id);
        await reports.AddAsync(bystander);

        await repository.DeleteAsync(resume.Id);

        (await reports.GetByIdAsync(derived.Id)).Should().BeNull("the resume it was derived from is gone");
        (await reports.GetPageByResumeIdAsync(resume.Id, PageRequests.Of())).Items
            .Should().BeEmpty("the history is not merely unreachable by id, it is gone from the list too");

        (await reports.GetByIdAsync(bystander.Id)).Should().Be(bystander);
        (await reports.GetPageByResumeIdAsync(survivor.Id, PageRequests.Of())).Items
            .Should().ContainSingle().Which.Should().Be(bystander);
    }
}

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
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
        var repository = new InMemoryResumeRepository();
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
        var repository = new InMemoryResumeRepository();
        var resume = CreateResume(AccountId.New());
        await repository.AddAsync(resume);

        await repository.DeleteAsync(resume.Id);

        (await repository.GetByIdAsync(resume.Id)).Should().BeNull();
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

    // The parity that makes every Api test meaningful. Api tests run against this store, so if it
    // answers the list ports in dictionary order — or hands out a cursor that skips a row — those tests
    // certify page behavior SQL Server has never produced. Same walk as
    // ResumeKeysetPaginationTests runs against a real database.
    [Fact]
    public async Task Resume_pages_walk_newest_first_without_a_gap_or_a_repeat()
    {
        var repository = new InMemoryResumeRepository();
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
        var repository = new InMemoryResumeRepository();
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
    // This store can only reproduce the first case, and the reason is stated in the repository: an
    // Analysis has no Delete() and no Status, and the only writer of its DeletedAt column is
    // ResumeRepository's cascade, which has no counterpart here — so a tombstoned row cannot exist for
    // an IsLive filter to hide. The second case is closed one layer up instead, by
    // GetAnalysisByIdHandler answering "Analysis not found." when the resume behind an analysis is gone;
    // GetAnalysisByIdHandlerTests pins that, and AnalysisRepositoryTests pins the EF half against a real
    // database.
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
}

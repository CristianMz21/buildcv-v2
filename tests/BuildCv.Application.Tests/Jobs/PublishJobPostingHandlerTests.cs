using BuildCv.Application.Jobs;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using FluentAssertions;

namespace BuildCv.Application.Tests.Jobs;

// PublishJobPostingHandler authorizes on CanPostJobs AND (ownership OR org Owner/Admin membership).
//
// The CanPostJobs half is the fix this PR carries: giving candidates ownership of Draft job offers
// (POST /job-offers/import) makes an ownership-only publish gate a way for a candidate to make their
// private offer public in one call, because a published posting is scored for any authenticated
// caller. Every test whose name ends "_IsForbidden" for a caller lacking CanPostJobs goes GREEN only
// because of that gate; the candidate-owner case in particular goes RED the moment it is removed.
public class PublishJobPostingHandlerTests
{
    private readonly FakeJobPostingRepository _jobPostings = new();
    private readonly FakeOrganizationRepository _organizations = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly PublishJobPostingHandler _handler;

    public PublishJobPostingHandlerTests() =>
        _handler = new PublishJobPostingHandler(_jobPostings, _organizations, _accounts);

    private async Task<Account> SeedAccountAsync(string email, Role role, bool locked = false)
    {
        var account = Account.Create(
            Email.Create(email),
            Password.Create("$argon2id$v=19$m=65536,t=3,p=1$saltsalt$somehashoutputbyteslong"),
            role);

        // Five failed logins is the lockout threshold (Account.MaxFailedAttempts). A locked recruiter
        // is the one CanPostJobs case that is a real account state rather than a role: CanPostJobs is
        // (Recruiter or Admin) AND Active AND !IsLocked.
        if (locked)
            for (var i = 0; i < 5; i++)
                account.RecordFailedLogin();

        await _accounts.AddAsync(account);
        return account;
    }

    private static JobPosting StandaloneDraft(AccountId ownerId) =>
        JobPosting.Create(ownerId, "Backend Engineer", OrganizationName.Create("Contoso"));

    [Fact]
    public async Task Publish_ByARecruiterOwningTheDraft_Succeeds()
    {
        var recruiter = await SeedAccountAsync("recruiter@example.com", Role.Recruiter);
        var posting = StandaloneDraft(recruiter.Id);
        await _jobPostings.AddAsync(posting);

        var result = await _handler.Handle(new PublishJobPostingCommand(recruiter.Id, posting.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(JobPostingStatus.Published);
    }

    // THE HOLE. A candidate owns a Draft offer they imported and tries to publish it. Removing the
    // CanPostJobs check turns this into a 200 that makes the offer world-scorable — which is exactly
    // why this test is the negative control for the guard.
    [Fact]
    public async Task Publish_ByACandidateOwningTheDraft_IsForbidden()
    {
        var candidate = await SeedAccountAsync("candidate@example.com", Role.Candidate);
        var posting = StandaloneDraft(candidate.Id);
        await _jobPostings.AddAsync(posting);

        var result = await _handler.Handle(new PublishJobPostingCommand(candidate.Id, posting.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");

        var reloaded = await _jobPostings.GetByIdAsync(posting.Id);
        reloaded!.Status.Should().Be(JobPostingStatus.Draft, "a refused publish must not have mutated the posting");
    }

    // A locked recruiter loses publish, because CanPostJobs includes Active && !IsLocked. This is a
    // fix, not a regression -- a locked account should not be publishing -- but it is a behaviour
    // change and belongs in the PR body, so it is pinned.
    [Fact]
    public async Task Publish_ByALockedRecruiterOwner_IsForbidden()
    {
        var recruiter = await SeedAccountAsync("locked@example.com", Role.Recruiter, locked: true);
        recruiter.CanPostJobs.Should().BeFalse("a locked recruiter is the state under test");
        var posting = StandaloneDraft(recruiter.Id);
        await _jobPostings.AddAsync(posting);

        var result = await _handler.Handle(new PublishJobPostingCommand(recruiter.Id, posting.Id));

        result.Error.Should().Be("Forbidden.");
    }

    // The org path still works for a qualified member: an org Admin who is a recruiter publishes a
    // posting the organization owns. Proves the CanPostJobs gate did not break the recruiter-team flow.
    [Fact]
    public async Task Publish_ByAnOrgAdminWhoIsARecruiter_Succeeds()
    {
        var founder = await SeedAccountAsync("founder@example.com", Role.Recruiter);
        var admin = await SeedAccountAsync("admin@example.com", Role.Recruiter);
        var organization = SeedOrganization(founder.Id, (admin.Id, MembershipRole.Admin));
        var posting = JobPosting.CreateForOrganization(founder.Id, organization.Id, "Backend Engineer");
        await _jobPostings.AddAsync(posting);

        var result = await _handler.Handle(new PublishJobPostingCommand(admin.Id, posting.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(JobPostingStatus.Published);
    }

    // THE ONE THAT IS NOT BEHAVIOUR-NEUTRAL. An org Owner/Admin whose account is a plain Candidate is
    // a reachable state -- CreateOrganizationHandler gates only on Active (any candidate can found an
    // org and become its Owner) and AddMemberHandler lets any Owner/Admin add another as Admin without
    // a role check. Such a member could publish an org posting BEFORE this PR and cannot after it. The
    // change is deliberate: publishing is a recruiter action, and CreateJobPostingHandler already
    // required CanPostJobs to create the same posting.
    [Fact]
    public async Task Publish_ByAnOrgAdminWithoutCanPostJobs_IsForbidden()
    {
        var founder = await SeedAccountAsync("founder@example.com", Role.Recruiter);
        var candidateAdmin = await SeedAccountAsync("candidate-admin@example.com", Role.Candidate);
        var organization = SeedOrganization(founder.Id, (candidateAdmin.Id, MembershipRole.Admin));
        var posting = JobPosting.CreateForOrganization(founder.Id, organization.Id, "Backend Engineer");
        await _jobPostings.AddAsync(posting);

        candidateAdmin.CanPostJobs.Should().BeFalse("this member is the non-neutral case the fix changes");

        var result = await _handler.Handle(new PublishJobPostingCommand(candidateAdmin.Id, posting.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
    }

    [Fact]
    public async Task Publish_ByAStrangerRecruiter_IsForbidden()
    {
        var owner = await SeedAccountAsync("owner@example.com", Role.Recruiter);
        var stranger = await SeedAccountAsync("stranger@example.com", Role.Recruiter);
        var posting = StandaloneDraft(owner.Id);
        await _jobPostings.AddAsync(posting);

        var result = await _handler.Handle(new PublishJobPostingCommand(stranger.Id, posting.Id));

        result.Error.Should().Be("Forbidden.");
    }

    // 404 before the authorization check, matching the create/score handlers.
    [Fact]
    public async Task Publish_UnknownPosting_IsNotFound()
    {
        var recruiter = await SeedAccountAsync("recruiter@example.com", Role.Recruiter);

        var result = await _handler.Handle(new PublishJobPostingCommand(recruiter.Id, JobPostingId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Job posting not found.");
    }

    private Organization SeedOrganization(AccountId founderId, params (AccountId Id, MembershipRole Role)[] members)
    {
        var organization = Organization.Create(
            OrganizationName.Create("Acme Inc"), Slug.Create("acme-inc"), founderId);
        foreach (var (id, role) in members)
            organization.AddMember(id, role);

        // The fake returns the object it stored, so this needs no async round trip; kept synchronous so
        // the org exists before the posting that references it is added.
        _organizations.AddAsync(organization).GetAwaiter().GetResult();
        return organization;
    }
}

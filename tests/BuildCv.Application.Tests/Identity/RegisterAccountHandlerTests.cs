using BuildCv.Application.Identity;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Application.Tests.Identity;

public class RegisterAccountHandlerTests
{
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly RegisterAccountHandler _handler;

    public RegisterAccountHandlerTests() =>
        _handler = new RegisterAccountHandler(_accounts, _hasher);

    [Fact]
    public async Task Register_success_returns_account_dto_and_persists_account()
    {
        var result = await _handler.Handle(new RegisterAccountCommand("new@example.com", "super-secret-password"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Email.Should().Be("new@example.com");
        result.Value.Role.Should().Be(nameof(Role.Candidate));
        result.Value.Status.Should().Be(nameof(AccountStatus.Active));
        result.Value.IsEmailVerified.Should().BeFalse();

        var persisted = await _accounts.GetByEmailAsync(Email.Create("new@example.com"));
        persisted.Should().NotBeNull();
        persisted!.Password.Hash.Should().NotBe("super-secret-password");
        persisted.Password.Hash.Should().StartWith("$argon2id$");
    }

    [Fact]
    public async Task Register_duplicate_email_fails()
    {
        await _handler.Handle(new RegisterAccountCommand("dup@example.com", "password-one"));

        var result = await _handler.Handle(new RegisterAccountCommand("dup@example.com", "password-two"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Email is already registered.");
    }

    [Fact]
    public async Task Register_invalid_email_fails()
    {
        var result = await _handler.Handle(new RegisterAccountCommand("not-an-email", "some-password"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(Role.Candidate)]
    [InlineData(Role.Recruiter)]
    public async Task Register_self_assignable_role_succeeds(Role role)
    {
        var result = await _handler.Handle(
            new RegisterAccountCommand($"{role}@example.com", "super-secret-password", role));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Role.Should().Be(role.ToString());
    }

    [Fact]
    public async Task Register_admin_role_fails_and_persists_nothing()
    {
        var result = await _handler.Handle(
            new RegisterAccountCommand("escalate@example.com", "super-secret-password", Role.Admin));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Role is not available for self-registration.");
        (await _accounts.GetByEmailAsync(Email.Create("escalate@example.com"))).Should().BeNull();
    }

    // THE RULING on issue #38, written down because an allowlist cannot say whether it was argued or
    // merely inherited. A stranger MAY self-assign Recruiter, deliberately: this is a free public
    // product, frictionless recruiter signup is the right default for one, and the spam a verification
    // gate would buy protection against only matters once there is a board to spam. Narrowing this to
    // Candidate is a product decision that has to be made again, out loud, and not a tightening that a
    // reader can assume was always intended.
    //
    // Widening it is caught here too, and that half is not hypothetical: Admin was self-assignable once
    // and this allowlist is what closed it.
    //
    // WHAT THE DECISION ACCEPTS, traced rather than assumed, so none of it is rediscovered as a
    // surprise. Recruiter is exactly Account.CanPostJobs, which is create-a-posting plus publish-it:
    //
    //   - A PUBLISHED POSTING IS READABLE BY EVERY AUTHENTICATED ACCOUNT. GetJobPostingHandler returns
    //     success for Published before it looks at the requester at all — the owner, admin and
    //     org-membership branches are all below that early return — so a published posting is readable
    //     by anyone who knows its id.
    //   - SCORING AGAINST ONE IS EQUALLY OPEN, but ScoreResumeHandler is NOT the same check and calling
    //     the two equally broad would be wrong in the direction that matters. It is owner-or-published
    //     with no admin and no organization escape, so it is strictly NARROWER than GetJobPosting for a
    //     posting that has not been published.
    //   - WHAT KEEPS THE AUDIENCE SMALL IS THE ABSENCE OF A BROWSE ENDPOINT, and it is still absent.
    //     GET /v1/job-offers is not one: it pages GetJobPostingsByOwner, filtered to the caller's own
    //     postings, so it lists nobody else's. Reaching a stranger's posting still needs its id.
    //   - MEMBERSHIP CANNOT BYPASS THE GATE for creating or publishing — both load the requester and
    //     check CanPostJobs first. But any ACTIVE account of any role may found an organization
    //     (CreateOrganizationHandler checks status, not role), an Owner or Admin may add any active
    //     account to it, and membership grants read access to that organization's unpublished drafts.
    //     So the read side of an org is reachable without Recruiter; only posting is not.
    //
    // RECORDED, NOT FIXED: CloseJobPostingHandler authorizes on ownership or org Owner/Admin and never
    // checks CanPostJobs, unlike publish. It is inert today because JobPosting.Close() refuses anything
    // that is not already Published, and only a CanPostJobs account could have published it. A note for
    // whoever changes either of those two things, not a defect.
    [Fact]
    public async Task Register_TheSelfAssignableRoles_AreExactlyCandidateAndRecruiter()
    {
        var accepted = new List<Role>();

        foreach (var role in Enum.GetValues<Role>())
        {
            var result = await _handler.Handle(
                new RegisterAccountCommand($"{role}@example.com", "super-secret-password", role));

            if (result.IsSuccess)
                accepted.Add(role);
            else
                result.Error.Should().Be("Role is not available for self-registration.");
        }

        accepted.Should().BeEquivalentTo(
            [Role.Candidate, Role.Recruiter],
            "keeping Recruiter open is a decision, and narrowing or widening the allowlist has to be "
            + "argued here rather than inferred from the code by the next reader");

        // The capability the decision actually hands to a stranger, asserted rather than described.
        // Role alone is not the grant — CanPostJobs is Recruiter AND Active AND not locked — so a
        // change that kept Recruiter self-assignable while moving what it buys would still land here.
        var recruiter = await _accounts.GetByEmailAsync(Email.Create($"{Role.Recruiter}@example.com"));
        recruiter!.CanPostJobs.Should().BeTrue(
            "a self-registered recruiter can create a job posting and publish it, with no verification "
            + "of any kind, and that is what this decision accepts");
    }

    [Fact]
    public async Task Register_undefined_role_value_fails()
    {
        // Enum.TryParse at the edge happily produces out-of-range values from numeric input,
        // so the handler must reject anything outside the self-assignable allowlist.
        var result = await _handler.Handle(
            new RegisterAccountCommand("weird@example.com", "super-secret-password", (Role)99));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Role is not available for self-registration.");
    }
}

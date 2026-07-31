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
    public async Task Resume_add_and_get_by_id_roundtrip()
    {
        var repository = new InMemoryResumeRepository();
        var ownerId = AccountId.New();
        var resume = CreateResume(ownerId);

        await repository.AddAsync(resume);
        var found = await repository.GetByIdAsync(resume.Id);
        var byOwner = await repository.GetByOwnerIdAsync(ownerId);

        found.Should().Be(resume);
        byOwner.Should().ContainSingle().Which.Should().Be(resume);
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
        var byOwner = await repository.GetByOwnerIdAsync(ownerId);

        found.Should().Be(jobPosting);
        byOwner.Should().ContainSingle().Which.Should().Be(jobPosting);
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
        var breakdown = ScoreBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.5, ScoringWeightsSnapshot.Default());
        var matching = Analysis.Create(AnalysisId.New(), breakdown, resumeId, JobPostingId.New(), DateTimeOffset.UtcNow);
        var other = Analysis.Create(AnalysisId.New(), breakdown, ResumeId.New(), JobPostingId.New(), DateTimeOffset.UtcNow);
        await repository.AddAsync(matching);
        await repository.AddAsync(other);

        var found = await repository.GetByResumeIdAsync(resumeId);

        found.Should().ContainSingle().Which.Should().Be(matching);
    }
}

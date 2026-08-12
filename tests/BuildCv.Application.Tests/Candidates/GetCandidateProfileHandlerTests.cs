using BuildCv.Application.Candidates;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Candidates;

public sealed class GetCandidateProfileHandlerTests
{
    private readonly FakeCandidateProfileRepository _profiles = new();
    private readonly GetCandidateProfileHandler _handler;

    public GetCandidateProfileHandlerTests() => _handler = new GetCandidateProfileHandler(_profiles);

    private static ContactInformation BuildContact(string name = "Jane Doe") =>
        new(PersonName.Create(name), Email.Create("jane@example.com"));

    [Fact]
    public async Task Get_profile_returns_the_profile_with_every_entry_id()
    {
        var ownerId = AccountId.New();
        var profile = CandidateProfile.Create(ownerId, BuildContact());
        profile.AddSkill(Skill.Create(Technology.Create("C#"), null, 5));
        profile.AddAward(new Award("Employee of the Year", null, null, null));
        await _profiles.AddAsync(profile);

        var result = await _handler.Handle(new GetCandidateProfileQuery(ownerId));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Profile.Should().BeSameAs(profile);
        result.Value.ItemIds.For(ResumeSection.Skills).Should().Equal(1);
        result.Value.ItemIds.For(ResumeSection.Awards).Should().Equal(1);
    }

    [Fact]
    public async Task Get_profile_when_none_exists_fails()
    {
        var result = await _handler.Handle(new GetCandidateProfileQuery(AccountId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Profile not found.");
    }

    // The 403 branch is structurally dead on every repository this port allows: the profile is looked
    // up BY the requester's own owner id, so a profile owned by somebody else can never be returned.
    // The guard is kept because it is the same sentence GetResume states, and a future read keyed by
    // profile id (an Admin view) is exactly the change that makes it load-bearing. Driving it needs a
    // store that VIOLATES the port — answers a foreign profile to any requester — which is what this
    // one does; the branch itself is what is under test.
    [Fact]
    public async Task Get_profile_when_the_store_returns_a_foreign_profile_fails_forbidden()
    {
        var ownerId = AccountId.New();
        var profile = CandidateProfile.Create(ownerId, BuildContact());
        await _profiles.AddAsync(profile);

        var loaded = await _profiles.GetByOwnerIdWithItemIdsAsync(ownerId);
        var wrongStore = new WrongKeyCandidateProfileRepository(loaded!);
        var handler = new GetCandidateProfileHandler(wrongStore);

        var result = await handler.Handle(new GetCandidateProfileQuery(AccountId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
    }

    /// <summary>
    /// A store that hands any requester a single pre-seeded profile. NOT a model of anything this
    /// system will run — the whole point is that no real store does this — but the only way to reach
    /// the handler's owner check with a mismatch.
    /// </summary>
    private sealed class WrongKeyCandidateProfileRepository(CandidateProfileWithItemIds seeded)
        : ICandidateProfileRepository
    {
        public Task<CandidateProfile?> GetByOwnerIdAsync(
            AccountId ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CandidateProfile?>(seeded.Profile);

        public Task<CandidateProfileWithItemIds?> GetByOwnerIdWithItemIdsAsync(
            AccountId ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CandidateProfileWithItemIds?>(seeded);

        public Task AddAsync(CandidateProfile profile, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateAsync(CandidateProfile profile, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

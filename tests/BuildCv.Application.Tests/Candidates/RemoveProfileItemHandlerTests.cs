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

public sealed class RemoveProfileItemHandlerTests
{
    private readonly FakeCandidateProfileRepository _profiles = new();
    private readonly RemoveProfileItemHandler _handler;

    public RemoveProfileItemHandlerTests() => _handler = new RemoveProfileItemHandler(_profiles);

    private static async Task<CandidateProfile> BuildProfileAsync(FakeCandidateProfileRepository profiles, AccountId ownerId)
    {
        var contact = new ContactInformation(
            PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
        var profile = CandidateProfile.Create(ownerId, contact);
        await profiles.AddAsync(profile);
        return profile;
    }

    [Fact]
    public async Task Remove_removes_the_entry_the_id_names()
    {
        var ownerId = AccountId.New();
        var profile = await BuildProfileAsync(_profiles, ownerId);
        profile.AddAward(new Award("Employee of the Year", null, null, null));
        profile.AddAward(new Award("Second", null, null, null));
        await _profiles.UpdateAsync(profile);

        var ids = (await _profiles.GetByOwnerIdWithItemIdsAsync(ownerId))!.ItemIds.For(ResumeSection.Awards);
        ids.Should().HaveCount(2);

        var result = await _handler.Handle(new RemoveProfileItemCommand(ownerId, ResumeSection.Awards, ids[1]));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Awards.Should().ContainSingle(x => x.Title == "Employee of the Year");
        (await _profiles.GetByOwnerIdAsync(ownerId))!.Awards.Should().ContainSingle();
    }

    [Fact]
    public async Task Remove_with_an_id_that_names_no_entry_fails_and_removes_nothing()
    {
        var ownerId = AccountId.New();
        var profile = await BuildProfileAsync(_profiles, ownerId);
        profile.AddAward(new Award("Employee of the Year", null, null, null));
        await _profiles.UpdateAsync(profile);

        var result = await _handler.Handle(new RemoveProfileItemCommand(ownerId, ResumeSection.Awards, 9999));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Awards entry not found.");
        (await _profiles.GetByOwnerIdAsync(ownerId))!.Awards.Should().ContainSingle();
    }

    [Fact]
    public async Task Remove_without_a_profile_fails_with_not_found()
    {
        var result = await _handler.Handle(new RemoveProfileItemCommand(AccountId.New(), ResumeSection.Awards, 1));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Profile not found.");
    }

    // Same promise as the resume twin, stated for the profile half: the profile is loaded BY the
    // requester's owner id, so a foreign account finds no profile at all and learns nothing about
    // whether an id is valid. The guard itself is exercised by the wrong-key store in
    // GetCandidateProfileHandlerTests; the route-level promise is that a foreign requester answers
    // "Profile not found.".
    [Fact]
    public async Task Remove_from_an_account_without_a_profile_never_names_anothers()
    {
        var ownerId = AccountId.New();
        var profile = await BuildProfileAsync(_profiles, ownerId);
        profile.AddAward(new Award("Employee of the Year", null, null, null));
        await _profiles.UpdateAsync(profile);
        var ids = (await _profiles.GetByOwnerIdWithItemIdsAsync(ownerId))!.ItemIds.For(ResumeSection.Awards);

        var result = await _handler.Handle(new RemoveProfileItemCommand(AccountId.New(), ResumeSection.Awards, ids[0]));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Profile not found.");
    }
}

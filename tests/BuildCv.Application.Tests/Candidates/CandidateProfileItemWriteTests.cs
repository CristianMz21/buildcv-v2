using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;
using ProfileCommands = BuildCv.Application.Candidates;

namespace BuildCv.Application.Tests.Candidates;

// The write every profile collection shares, driven through one handler. The suite's job is to prove
// the SHARED core's promises hold for the profile half too: append adds, replace removes-before-adds
// in one save, an unknown id answers "not found" rather than forbidden, and a missing profile is a 404
// on every route that needs one.
public sealed class CandidateProfileItemWriteTests
{
    private readonly FakeCandidateProfileRepository _profiles = new();
    private readonly ProfileCommands.AddAwardHandler _handler;

    public CandidateProfileItemWriteTests() => _handler = new ProfileCommands.AddAwardHandler(_profiles);

    private static async Task<CandidateProfile> BuildProfileAsync(FakeCandidateProfileRepository profiles, AccountId ownerId)
    {
        var contact = new ContactInformation(
            PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
        var profile = CandidateProfile.Create(ownerId, contact);
        await profiles.AddAsync(profile);
        return profile;
    }

    private static ProfileCommands.AddAwardCommand Append(AccountId ownerId, string title) =>
        new(ownerId, title, null, null, null);

    // One successful append is exactly one UpdateAsync — the write the ItemWrite core promises, rather
    // than the load being an implicit write or a second save creeping in. (This is not the
    // build-before-load rejection property: that one needs a value whose constructor throws and a
    // WriteCount that stays put, and lives with the generic core's own tests.)
    [Fact]
    public async Task Append_OneSuccessfulAppend_IsOneUpdateAsync()
    {
        var ownerId = AccountId.New();
        var profile = await BuildProfileAsync(_profiles, ownerId);
        var writesBefore = _profiles.WriteCount;

        var result = await _handler.Handle(Append(ownerId, "Employee of the Year"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Awards.Should().ContainSingle();
        _profiles.WriteCount.Should().Be(writesBefore + 1, "one successful append is one UpdateAsync");
        profile.Awards.Should().HaveCount(1, "the profile is written into the store");
    }

    [Fact]
    public async Task Replace_removes_before_it_adds_inside_one_save()
    {
        var ownerId = AccountId.New();
        var profile = await BuildProfileAsync(_profiles, ownerId);
        await _handler.Handle(Append(ownerId, "First"));
        var ids = await _profiles.GetByOwnerIdWithItemIdsAsync(ownerId);
        var firstId = ids!.ItemIds.For(ResumeSection.Awards)[0];

        var writesBefore = _profiles.WriteCount;
        var result = await _handler.Handle(new ProfileCommands.AddAwardCommand(ownerId, "Second", null, null, null, firstId));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Awards.Should().ContainSingle(x => x.Title == "Second");
        result.Value.Awards.Should().NotContain(x => x.Title == "First");
        _profiles.WriteCount.Should().Be(writesBefore + 1, "a replace is ONE UpdateAsync, not remove-then-add");
    }

    [Fact]
    public async Task Replace_with_an_id_that_names_no_entry_fails_and_removes_nothing()
    {
        var ownerId = AccountId.New();
        var profile = await BuildProfileAsync(_profiles, ownerId);
        await _handler.Handle(Append(ownerId, "Employee of the Year"));

        var result = await _handler.Handle(new ProfileCommands.AddAwardCommand(ownerId, "Second", null, null, null, 9999));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Awards entry not found.");
        (await _profiles.GetByOwnerIdAsync(ownerId))!.Awards.Should().ContainSingle(x => x.Title == "Employee of the Year");
    }

    [Fact]
    public async Task Append_without_a_profile_fails_with_not_found()
    {
        var result = await _handler.Handle(Append(AccountId.New(), "Employee of the Year"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Profile not found.");
    }

    // Idempotent Add, not an error: the profile is written to from several directions and a repeat must
    // not fail an import of a second CV that shares most of its content with the first.
    [Fact]
    public async Task Append_a_duplicate_is_a_no_op_not_an_error()
    {
        var ownerId = AccountId.New();
        var profile = await BuildProfileAsync(_profiles, ownerId);

        var first = await _handler.Handle(Append(ownerId, "Employee of the Year"));
        var second = await _handler.Handle(Append(ownerId, "Employee of the Year"));

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        (await _profiles.GetByOwnerIdAsync(ownerId))!.Awards.Should().ContainSingle();
    }
}

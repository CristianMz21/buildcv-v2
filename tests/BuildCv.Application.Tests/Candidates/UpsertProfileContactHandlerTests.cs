using BuildCv.Application.Candidates;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Candidates;

public sealed class UpsertProfileContactHandlerTests
{
    private readonly FakeCandidateProfileRepository _profiles = new();
    private readonly UpsertProfileContactHandler _handler;

    public UpsertProfileContactHandlerTests() => _handler = new UpsertProfileContactHandler(_profiles);

    private static UpsertProfileContactCommand BuildCommand(AccountId requesterId) =>
        new(requesterId, "Jane Doe", "jane@example.com", "+5491155550100", "Buenos Aires", "Backend engineer");

    [Fact]
    public async Task Upsert_contact_creates_the_profile_on_first_call()
    {
        var ownerId = AccountId.New();
        var writesBefore = _profiles.WriteCount;

        var result = await _handler.Handle(BuildCommand(ownerId));

        result.IsSuccess.Should().BeTrue();
        result.Value!.OwnerId.Should().Be(ownerId);
        result.Value.ContactInformation.FullName.Value.Should().Be("Jane Doe");
        (await _profiles.GetByOwnerIdAsync(ownerId)).Should().NotBeNull();
        _profiles.WriteCount.Should().Be(writesBefore + 1, "a create is one AddAsync");
    }

    [Fact]
    public async Task Upsert_contact_updates_the_profile_on_later_calls()
    {
        var ownerId = AccountId.New();
        await _handler.Handle(BuildCommand(ownerId));

        var result = await _handler.Handle(
            new UpsertProfileContactCommand(ownerId, "Jane Doe", "jane@example.com", null, null, "Senior engineer"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.ContactInformation.Summary.Should().Be("Senior engineer");
        result.Value.ContactInformation.Location.Should().BeNull();
        (await _profiles.GetByOwnerIdAsync(ownerId))!.ContactInformation.Summary.Should().Be("Senior engineer");
    }

    // Website and Profiles are NOT accepted by this command and must survive an update untouched — a
    // "not sent" field means "unchanged", exactly as UpdateContactInformationHandler holds for a resume.
    // The import path is the only writer of those two, so silently rebuilding the contact from this
    // command's shape would erase them.
    [Fact]
    public async Task Upsert_contact_carries_website_and_profiles_over_from_the_stored_contact()
    {
        var ownerId = AccountId.New();
        var created = (await _handler.Handle(BuildCommand(ownerId))).Value!;
        var website = Url.Create("https://jane.dev");
        var profiles = new List<Profile>
        {
            new("github", null, Url.Create("https://github.com/jane")),
        };
        created.UpdateContactInformation(created.ContactInformation with { Website = website, Profiles = profiles });
        await _profiles.UpdateAsync(created);

        var result = await _handler.Handle(BuildCommand(ownerId));

        result.Value!.ContactInformation.Website.Should().Be(website);
        result.Value.ContactInformation.Profiles.Should().Equal(profiles);
    }

    [Fact]
    public async Task Upsert_contact_with_an_invalid_email_fails()
    {
        var result = await _handler.Handle(
            new UpsertProfileContactCommand(AccountId.New(), "Jane", "not-an-email", null, null, null));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        _profiles.WriteCount.Should().Be(0, "a rejected contact must not create a profile");
    }
}

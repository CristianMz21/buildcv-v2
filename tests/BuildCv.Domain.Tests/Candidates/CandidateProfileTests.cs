using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Candidates;

public class CandidateProfileTests
{
    private static ContactInformation SomeContact() =>
        new(FullName: PersonName.Create("Sam Doe"), Email: Email.Create("sam@example.com"));

    private static CandidateProfile SomeProfile() =>
        CandidateProfile.Create(AccountId.New(), SomeContact());

    private static Experience SomeExperience(string position = "Backend Engineer") =>
        new(
            Type: ExperienceType.Professional,
            Organization: OrganizationName.Create("Globant"),
            Position: position,
            Period: DateRange.Create(DateOnly.Parse("2020-01-01")));

    [Fact]
    public void Create_StartsEmpty_AndIsOwnedByTheAccount()
    {
        var owner = AccountId.New();

        var profile = CandidateProfile.Create(owner, SomeContact());

        profile.OwnerId.Should().Be(owner);
        profile.Experiences.Should().BeEmpty();
        profile.Skills.Should().BeEmpty();
    }

    // THE DIFFERENCE FROM A RESUME, and the reason this aggregate has its own Add methods rather than
    // reusing Resume's. A CV is authored once and refuses a duplicate to catch a typo. A profile is
    // written to repeatedly and from several directions — a second CV imported, a field typed by hand,
    // the same PDF uploaded again after a correction — so refusing would make importing a second CV fail
    // on everything the two share, which is most of it.
    [Fact]
    public void AddingTheSameEntryTwice_IsANoOp_RatherThanAnError()
    {
        var profile = SomeProfile();
        var experience = SomeExperience();

        profile.AddExperience(experience);
        profile.AddExperience(experience);

        profile.Experiences.Should().ContainSingle();
    }

    // Deliberately conservative: equality is the record's own, so a role whose dates or bullets differ is
    // a DIFFERENT entry. A false duplicate would silently lose information the candidate gave us, which
    // is the one thing this aggregate exists to prevent.
    [Fact]
    public void AnEntryThatDiffersInAnyField_IsKeptAsItsOwn()
    {
        var profile = SomeProfile();

        profile.AddExperience(SomeExperience());
        profile.AddExperience(SomeExperience(position: "Senior Backend Engineer"));

        profile.Experiences.Should().HaveCount(2);
    }

    // BY POSITION, exactly as ResumeItems.RemoveAt does: these are value objects with no identity, so
    // removing "by value" would delete an entry the caller never named whenever two happen to be equal.
    [Fact]
    public void RemoveAt_TakesTheEntryAtThatPosition()
    {
        var profile = SomeProfile();
        profile.AddExperience(SomeExperience(position: "First"));
        profile.AddExperience(SomeExperience(position: "Second"));

        profile.RemoveExperienceAt(0).Should().BeTrue();

        profile.Experiences.Should().ContainSingle()
            .Which.Position.Should().Be("Second");
    }

    [Fact]
    public void RemoveAt_OutOfRange_ChangesNothing()
    {
        var profile = SomeProfile();
        profile.AddExperience(SomeExperience());

        profile.RemoveExperienceAt(5).Should().BeFalse();
        profile.Experiences.Should().ContainSingle();
    }

    // A no-op must not look like a write: UpdatedAt is what a future "your profile changed since this CV
    // was generated" notice would read, and bumping it on a duplicate import would announce a change
    // that did not happen.
    [Fact]
    public void AddingADuplicate_DoesNotTouchUpdatedAt()
    {
        var profile = SomeProfile();
        var experience = SomeExperience();
        profile.AddExperience(experience);
        var after = profile.UpdatedAt;

        profile.AddExperience(experience);

        profile.UpdatedAt.Should().Be(after);
    }

    [Fact]
    public void EveryCollectionAcceptsItsOwnType()
    {
        var profile = SomeProfile();

        profile.AddSkill(Skill.Create(Technology.Create("C#")));
        profile.AddLanguage(Language.Create("Spanish", "Native"));
        profile.AddInterest(new Interest("Cycling"));

        profile.Skills.Should().ContainSingle();
        profile.Languages.Should().ContainSingle();
        profile.Interests.Should().ContainSingle();
    }
}

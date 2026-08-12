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

    // The same rule, on the contact: the import path calls UpdateContactInformation with the result of
    // a gap-fill merge, and on a no-op re-import that result is EQUAL to what the profile already holds
    // but is a NEW instance — GapFill returns a fresh record. Touching on that would announce a contact
    // change the import did not make.
    [Fact]
    public void UpdateContactInformation_WithAnEqualContactBuiltFreshly_DoesNotTouchUpdatedAt()
    {
        var profile = SomeProfile();
        var after = profile.UpdatedAt;

        profile.UpdateContactInformation(new ContactInformation(
            FullName: profile.ContactInformation.FullName,
            Email: profile.ContactInformation.Email));

        profile.UpdatedAt.Should().Be(after);
    }

    // THE BOUNDARY THE TESTS ABOVE DO NOT CROSS. They add the SAME instance twice, which is the same
    // list instance twice, so record equality passes without noticing that its IReadOnlyList members
    // compare BY REFERENCE. A re-import builds every entry fresh, lists included, so the profile's
    // idempotence has to survive two instances holding the same contents. These four types are the ones
    // that carry such a member, and each pair here is equal content in two different list instances.
    public static TheoryData<object, object> EqualContentInTwoListInstances() =>
        new()
        {
            {
                SomeExperience() with { Highlights = ["Cut latency in half"] },
                SomeExperience() with { Highlights = ["Cut latency in half"] }
            },
            {
                new Project("buildcv", DateRange.Create(DateOnly.Parse("2024-01-01")))
                {
                    Technologies = [Technology.Create("C#")],
                    Highlights = ["Deterministic"],
                },
                new Project("buildcv", DateRange.Create(DateOnly.Parse("2024-01-01")))
                {
                    Technologies = [Technology.Create("C#")],
                    Highlights = ["Deterministic"],
                }
            },
            {
                Skill.Create(Technology.Create("C#"), SkillLevel.Advanced, 7) with { Keywords = ["aspnet"] },
                Skill.Create(Technology.Create("C#"), SkillLevel.Advanced, 7) with { Keywords = ["aspnet"] }
            },
            {
                new Interest("Climbing") with { Keywords = ["bouldering"] },
                new Interest("Climbing") with { Keywords = ["bouldering"] }
            }
        };

    [Theory]
    [MemberData(nameof(EqualContentInTwoListInstances))]
    public void TwoEntriesWithTheSameContentInDifferentListInstances_AreEqualAndHashEqual(object left, object right)
    {
        left.Should().Be(right);
        left.GetHashCode().Should().Be(right.GetHashCode());
    }

    // Adding the same entry to a profile that already holds it stays a no-op when the "same" entry is a
    // fresh build with new list instances — the ordinary shape across two imports, and the case the
    // record-level sequence equality exists for. The same-instance tests above would pass without it.
    [Fact]
    public void AddingAnEqualEntryBuiltFreshly_IsANoOp()
    {
        var profile = SomeProfile();
        var experience = SomeExperience() with { Highlights = ["Cut latency in half"] };

        profile.AddExperience(experience);
        var after = profile.UpdatedAt;
        profile.AddExperience(experience with { Highlights = ["Cut latency in half"] });

        profile.Experiences.Should().ContainSingle();
        profile.UpdatedAt.Should().Be(after, "a no-op must not look like a write");
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

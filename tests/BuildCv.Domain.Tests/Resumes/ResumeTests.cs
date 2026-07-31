using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Resumes;

public class ResumeTests
{
    [Fact]
    public void Resume_with_minimal_data_can_be_created()
    {
        var ownerId = AccountId.New();
        var contact = new ContactInformation(
            FullName: PersonName.Create("Cristian Arellano"),
            Email: Email.Create("cristian@example.com"));

        var resume = Resume.Create(ownerId, contact);

        resume.Id.Should().NotBeNull();
        resume.ContactInformation.FullName.Value.Should().Be("Cristian Arellano");
        resume.Experiences.Should().BeEmpty();
        resume.Skills.Should().BeEmpty();
        resume.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public void Resume_with_work_experiences_can_be_created()
    {
        var ownerId = AccountId.New();
        var contact = new ContactInformation(
            FullName: PersonName.Create("Cristian Arellano"),
            Email: Email.Create("cristian@example.com"),
            PhoneNumber: PhoneNumber.Create("+573001234567"),
            Location: "Bogotá, Colombia",
            Summary: "Senior .NET Developer");

        var work = new Experience(
            Type: ExperienceType.Professional,
            Organization: OrganizationName.Create("TechCorp"),
            Position: "Senior .NET Developer",
            Period: DateRange.Create(DateOnly.Parse("2022-01-01")),
            Summary: "Led backend team")
        {
            Highlights = ["Improved performance by 40%", "Mentored 3 junior devs"]
        };

        var resume = Resume.Create(ownerId, contact);
        resume.AddWorkExperience(work);
        resume.AddSkill(Skill.Create(Technology.Create("C#"), SkillLevel.Expert));

        resume.Experiences.Should().HaveCount(1);
        resume.Experiences[0].Organization.Value.Should().Be("TechCorp");
        resume.Skills.Should().HaveCount(1);
    }

    [Fact]
    public void Resume_null_owner_throws()
    {
        var contact = new ContactInformation(
            FullName: PersonName.Create("Cristian Arellano"),
            Email: Email.Create("cristian@example.com"));

        var act = () => Resume.Create(null!, contact);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resume_null_contact_information_throws()
    {
        var ownerId = AccountId.New();

        var act = () => Resume.Create(ownerId, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resume_adding_duplicate_skill_throws_duplicate_skill()
    {
        var ownerId = AccountId.New();
        var contact = new ContactInformation(
            FullName: PersonName.Create("Cristian Arellano"),
            Email: Email.Create("cristian@example.com"));
        var resume = Resume.Create(ownerId, contact);

        resume.AddSkill(Skill.Create(Technology.Create("C#")));

        var act = () => resume.AddSkill(Skill.Create(Technology.Create("c#")));

        act.Should().Throw<DuplicateSkillException>();
    }

    [Fact]
    public void Resume_remove_experience_removes_the_given_experience()
    {
        var ownerId = AccountId.New();
        var contact = new ContactInformation(
            FullName: PersonName.Create("Cristian Arellano"),
            Email: Email.Create("cristian@example.com"));
        var resume = Resume.Create(ownerId, contact);
        var work = new Experience(
            Type: ExperienceType.Professional,
            Organization: OrganizationName.Create("TechCorp"),
            Position: "Senior .NET Developer",
            Period: DateRange.Create(DateOnly.Parse("2022-01-01")));
        resume.AddWorkExperience(work);

        resume.RemoveExperience(work);

        resume.Experiences.Should().BeEmpty();
    }

    [Fact]
    public void Resume_distinct_instances_are_not_equal()
    {
        var ownerId = AccountId.New();
        var contact = new ContactInformation(
            FullName: PersonName.Create("Cristian Arellano"),
            Email: Email.Create("cristian@example.com"));

        var resume1 = Resume.Create(ownerId, contact);
        var resume2 = Resume.Create(ownerId, contact);

        resume1.Should().NotBe(resume2);
        resume1.Equals(resume1).Should().BeTrue();
    }
}

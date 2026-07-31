using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Resumes;

public class ResumeTests
{
    [Fact]
    public void Resume_with_minimal_data_can_be_created()
    {
        var contact = new ContactInformation(
            FullName: PersonName.Create("Cristian Arellano"),
            Email: Email.Create("cristian@example.com"));

        var resume = Resume.Create(contact);

        resume.ContactInformation.FullName.Value.Should().Be("Cristian Arellano");
        resume.Experiences.Should().BeEmpty();
        resume.Skills.Should().BeEmpty();
    }

    [Fact]
    public void Resume_with_work_experiences_can_be_created()
    {
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
            Period: new DateRange(DateOnly.Parse("2022-01-01"), null),
            Summary: "Led backend team")
        {
            Highlights = ["Improved performance by 40%", "Mentored 3 junior devs"]
        };

        var resume = Resume.Create(contact);
        resume.AddWorkExperience(work);
        resume.AddSkill(new Skill(Technology.Create("C#"), "Expert"));

        resume.Experiences.Should().HaveCount(1);
        resume.Experiences[0].Organization.Value.Should().Be("TechCorp");
        resume.Skills.Should().HaveCount(1);
    }

    [Fact]
    public void Resume_is_immutable()
    {
        var contact1 = new ContactInformation(
            FullName: PersonName.Create("Cristian Arellano"),
            Email: Email.Create("cristian@example.com"));

        var contact2 = new ContactInformation(
            FullName: PersonName.Create("Cristian Arellano Muñoz"),
            Email: Email.Create("cristian@example.com"));

        var resume1 = Resume.Create(contact1);
        var resume2 = Resume.Create(contact2);

        resume1.ContactInformation.FullName.Value.Should().Be("Cristian Arellano");
        resume2.ContactInformation.FullName.Value.Should().Be("Cristian Arellano Muñoz");
    }
}

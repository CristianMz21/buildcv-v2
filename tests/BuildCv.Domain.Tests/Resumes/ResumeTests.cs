using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Resumes;

public class ResumeTests
{
    [Fact]
    public void Resume_with_minimal_data_can_be_created()
    {
        var basics = new Basics(
            FullName: "Cristian Arellano",
            Email: "cristian@example.com",
            PhoneNumber: null,
            Location: null,
            Website: null,
            Summary: null,
            PersonalInformation: null,
            Profiles: []);

        var resume = new Resume(
            Basics: basics,
            WorkExperiences: [],
            Educations: [],
            Skills: [],
            Projects: [],
            Certificates: [],
            Languages: [],
            Awards: [],
            Publications: [],
            VolunteerExperiences: [],
            Interests: [],
            References: []);

        resume.Basics.FullName.Should().Be("Cristian Arellano");
        resume.WorkExperiences.Should().BeEmpty();
        resume.Skills.Should().BeEmpty();
    }

    [Fact]
    public void Resume_with_work_experiences_can_be_created()
    {
        var basics = new Basics(
            FullName: "Cristian Arellano",
            Email: "cristian@example.com",
            PhoneNumber: "+57 300 1234567",
            Location: "Bogotá, Colombia",
            Website: null,
            Summary: "Senior .NET Developer",
            PersonalInformation: null,
            Profiles: []);

        var work = new WorkExperience(
            Company: "TechCorp",
            Position: "Senior .NET Developer",
            Period: new DateRange(Start: "2022-01", End: null),
            Summary: "Led backend team",
            Highlights: ["Improved performance by 40%", "Mentored 3 junior devs"]);

        var resume = new Resume(
            Basics: basics,
            WorkExperiences: [work],
            Educations: [],
            Skills: [new Skill("C#", "Expert", [])],
            Projects: [],
            Certificates: [],
            Languages: [],
            Awards: [],
            Publications: [],
            VolunteerExperiences: [],
            Interests: [],
            References: []);

        resume.WorkExperiences.Should().HaveCount(1);
        resume.WorkExperiences[0].Company.Should().Be("TechCorp");
        resume.Skills.Should().HaveCount(1);
    }

    [Fact]
    public void Resume_is_immutable()
    {
        var basics = new Basics(
            FullName: "Cristian Arellano",
            Email: "cristian@example.com",
            PhoneNumber: null,
            Location: null,
            Website: null,
            Summary: null,
            PersonalInformation: null,
            Profiles: []);

        var resume1 = new Resume(
            Basics: basics,
            WorkExperiences: [],
            Educations: [],
            Skills: [],
            Projects: [],
            Certificates: [],
            Languages: [],
            Awards: [],
            Publications: [],
            VolunteerExperiences: [],
            Interests: [],
            References: []);

        var resume2 = resume1 with
        {
            Basics = basics with { FullName = "Cristian Arellano Muñoz" }
        };

        resume1.Basics.FullName.Should().Be("Cristian Arellano");
        resume2.Basics.FullName.Should().Be("Cristian Arellano Muñoz");
    }
}

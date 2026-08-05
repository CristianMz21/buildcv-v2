using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Resumes;

public class ResumeCollectionTests
{
    private static Resume BuildResume()
    {
        var contact = new ContactInformation(
            FullName: PersonName.Create("Jane Doe"),
            Email: Email.Create("jane@example.com"));
        return Resume.Create(AccountId.New(), contact);
    }

    private static Education BuildEducation(string institution = "MIT", string? degree = "BSc") =>
        new(OrganizationName.Create(institution), degree, "Computer Science",
            DateRange.Create(DateOnly.Parse("2018-01-01")), null);

    private static Project BuildProject(string name = "Side project") =>
        new(name, DateRange.Create(DateOnly.Parse("2023-01-01")))
        {
            Technologies = [Technology.Create("dotnet")],
        };

    private static Certificate BuildCertificate(string name = "AWS Solutions Architect") =>
        new(name, OrganizationName.Create("Amazon"), null, null, null);

    [Fact]
    public void Resume_add_education_adds_and_bumps_updated_at()
    {
        var resume = BuildResume();
        var before = resume.UpdatedAt;

        resume.AddEducation(BuildEducation());

        resume.Educations.Should().HaveCount(1);
        resume.Educations[0].Institution.Value.Should().Be("MIT");
        resume.UpdatedAt.Should().BeAfter(before);
    }

    [Fact]
    public void Resume_add_project_adds_and_bumps_updated_at()
    {
        var resume = BuildResume();
        var before = resume.UpdatedAt;

        resume.AddProject(BuildProject());

        resume.Projects.Should().HaveCount(1);
        resume.Projects[0].Technologies.Should().HaveCount(1);
        resume.UpdatedAt.Should().BeAfter(before);
    }

    [Fact]
    public void Resume_add_certificate_adds_and_bumps_updated_at()
    {
        var resume = BuildResume();
        var before = resume.UpdatedAt;

        resume.AddCertificate(BuildCertificate());

        resume.Certificates.Should().HaveCount(1);
        resume.Certificates[0].Name.Should().Be("AWS Solutions Architect");
        resume.UpdatedAt.Should().BeAfter(before);
    }

    [Fact]
    public void Resume_adding_duplicate_certificate_throws_duplicate_entry()
    {
        var resume = BuildResume();
        resume.AddCertificate(BuildCertificate("AWS Solutions Architect"));

        var act = () => resume.AddCertificate(BuildCertificate("aws solutions architect"));

        act.Should().Throw<DuplicateEntryException>();
    }

    [Fact]
    public void Resume_adding_duplicate_language_throws_duplicate_entry()
    {
        var resume = BuildResume();
        resume.AddLanguage(Language.Create("English", "Native"));

        var act = () => resume.AddLanguage(Language.Create("english", "Fluent"));

        act.Should().Throw<DuplicateEntryException>();
    }

    [Fact]
    public void Resume_remove_education_removes_and_bumps_updated_at()
    {
        var resume = BuildResume();
        var education = BuildEducation();
        resume.AddEducation(education);
        var before = resume.UpdatedAt;

        resume.RemoveEducation(education);

        resume.Educations.Should().BeEmpty();
        resume.UpdatedAt.Should().BeAfter(before);
    }

    [Fact]
    public void Resume_remove_missing_education_throws_entry_not_found()
    {
        var resume = BuildResume();

        var act = () => resume.RemoveEducation(BuildEducation());

        act.Should().Throw<EntryNotFoundException>();
    }

    [Fact]
    public void Resume_remove_missing_skill_throws_entry_not_found()
    {
        var resume = BuildResume();

        var act = () => resume.RemoveSkill("C#");

        act.Should().Throw<EntryNotFoundException>();
    }

    [Fact]
    public void Resume_skills_view_reflects_subsequent_additions()
    {
        var resume = BuildResume();
        var view = resume.Skills;

        resume.AddSkill(Skill.Create(Technology.Create("C#")));

        view.Should().HaveCount(1);
    }
}

using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Resumes;

public class AddEducationHandlerTests
{
    private readonly FakeResumeRepository _resumes = new();
    private readonly AddEducationHandler _handler;

    public AddEducationHandlerTests() => _handler = new AddEducationHandler(_resumes);

    [Fact]
    public async Task Add_education_success_adds_education_to_resume()
    {
        var ownerId = AccountId.New();
        var contact = new ContactInformation(PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
        var resume = Resume.Create(ownerId, contact);
        await _resumes.AddAsync(resume);

        var result = await _handler.Handle(new AddEducationCommand(
            ownerId, resume.Id, "MIT", "BSc", "Computer Science",
            new DateOnly(2018, 1, 1), new DateOnly(2022, 1, 1), "4.0 GPA", EducationLevel.Bachelor));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Educations.Should().HaveCount(1);
        result.Value.Educations[0].Institution.Value.Should().Be("MIT");
        result.Value.Educations[0].Degree.Should().Be("BSc");
        result.Value.Educations[0].Period.EndsOn.Should().Be(new DateOnly(2022, 1, 1));
        result.Value.Educations[0].Level.Should().Be(EducationLevel.Bachelor);
        (await _resumes.GetByIdAsync(resume.Id))!.Educations.Should().HaveCount(1);
    }

    // The level is optional all the way down. A resume that names a degree but no level is missing
    // DATA, and PR 3 turns that into a recommendation — so null has to survive the handler as null
    // rather than being defaulted to the bottom rung, which would read as "high school" and score the
    // candidate down for it.
    [Fact]
    public async Task Add_education_without_a_level_stores_no_level()
    {
        var ownerId = AccountId.New();
        var contact = new ContactInformation(PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
        var resume = Resume.Create(ownerId, contact);
        await _resumes.AddAsync(resume);

        var result = await _handler.Handle(new AddEducationCommand(
            ownerId, resume.Id, "MIT", "Ingeniero en Sistemas", null,
            new DateOnly(2018, 1, 1), null, null, null));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Educations[0].Level.Should().BeNull();
        result.Value.Educations[0].Degree.Should().Be("Ingeniero en Sistemas",
            "the free text is kept verbatim and never parsed into a level");
    }
}

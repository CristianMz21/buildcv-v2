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
            new DateOnly(2018, 1, 1), new DateOnly(2022, 1, 1), "4.0 GPA"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Educations.Should().HaveCount(1);
        result.Value.Educations[0].Institution.Value.Should().Be("MIT");
        result.Value.Educations[0].Degree.Should().Be("BSc");
        result.Value.Educations[0].Period.End.Should().Be(new DateOnly(2022, 1, 1));
        (await _resumes.GetByIdAsync(resume.Id))!.Educations.Should().HaveCount(1);
    }
}

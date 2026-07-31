using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Resumes;

public class AddCertificateHandlerTests
{
    private readonly FakeResumeRepository _resumes = new();
    private readonly AddCertificateHandler _handler;

    public AddCertificateHandlerTests() => _handler = new AddCertificateHandler(_resumes);

    private static Resume BuildResume(AccountId ownerId)
    {
        var contact = new ContactInformation(PersonName.Create("Jane Doe"), Email.Create("jane@example.com"));
        return Resume.Create(ownerId, contact);
    }

    private static AddCertificateCommand BuildCommand(AccountId requesterId, ResumeId resumeId) =>
        new(requesterId, resumeId, "AWS Solutions Architect", "Amazon",
            "cred-123", "https://aws.example.com/cred-123",
            new DateOnly(2024, 1, 1), null);

    [Fact]
    public async Task Add_certificate_success_adds_certificate_to_resume()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId);
        await _resumes.AddAsync(resume);

        var result = await _handler.Handle(BuildCommand(ownerId, resume.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Certificates.Should().HaveCount(1);
        result.Value.Certificates[0].Name.Should().Be("AWS Solutions Architect");
        result.Value.Certificates[0].CredentialUrl!.Value.Should().Be("https://aws.example.com/cred-123");
        (await _resumes.GetByIdAsync(resume.Id))!.Certificates.Should().HaveCount(1);
    }

    [Fact]
    public async Task Add_certificate_forbidden_when_requester_is_not_owner()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId);
        await _resumes.AddAsync(resume);

        var result = await _handler.Handle(BuildCommand(AccountId.New(), resume.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
    }

    [Fact]
    public async Task Add_certificate_resume_not_found_fails()
    {
        var result = await _handler.Handle(BuildCommand(AccountId.New(), ResumeId.New()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Resume not found.");
    }

    [Fact]
    public async Task Add_certificate_duplicate_name_fails()
    {
        var ownerId = AccountId.New();
        var resume = BuildResume(ownerId);
        await _resumes.AddAsync(resume);
        await _handler.Handle(BuildCommand(ownerId, resume.Id));

        var result = await _handler.Handle(BuildCommand(ownerId, resume.Id));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("AWS Solutions Architect");
    }
}

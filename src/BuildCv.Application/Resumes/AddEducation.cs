namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddEducationCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Institution,
    string? Degree,
    string? FieldOfStudy,
    DateOnly Start,
    DateOnly? End,
    string? Grade,
    EducationLevel? Level,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddEducationHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddEducationCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddEducationCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
            ResumeSection.Educations,
            command.ReplacingItemId,
            () =>
            {
                var education = new Education(
                    OrganizationName.Create(command.Institution),
                    command.Degree,
                    command.FieldOfStudy,
                    DateRange.Create(command.Start, command.End),
                    command.Grade,
                    command.Level);
                return resume => resume.AddEducation(education);
            },
            cancellationToken);
}
